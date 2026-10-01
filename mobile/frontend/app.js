(() => {
  'use strict';

  /* ───────────────────────── 基础工具 ───────────────────────── */

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const icon = name => `<svg aria-hidden="true"><use href="#i-${name}"/></svg>`;
  const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[ch]));
  const clamp = (value, min, max) => Math.max(min, Math.min(max, Number(value) || min));
  const setText = (target, value) => {
    const node = typeof target === 'string' ? $(target) : target;
    const next = String(value ?? '');
    if (node && node.textContent !== next) node.textContent = next;
  };
  const formatTime = value => {
    const date = value ? new Date(value) : new Date();
    if (Number.isNaN(date.getTime())) return '—';
    return date.toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' });
  };
  const statusLabel = status => ({ queued: '等待中', running: '下载中', success: '已完成', completed: '已完成', failed: '异常', cancelled: '已取消', stopping: '正在停止' }[status] || status || '等待中');
  const successStatus = status => status === 'success' || status === 'completed' || status === 'done';
  const haptic = ms => {
    if (store.raw('jm-haptics-v1', '1') === '0') return;
    let browserFeedback = false;
    try { browserFeedback = navigator.vibrate?.(ms) === true; } catch { }
    if (!browserFeedback && android && ms >= 8) {
      try { nativeCall('haptic', { kind: ms >= 16 ? 'strong' : 'light' }); } catch { }
    }
  };

  const store = {
    get(key, fallback) {
      try { const value = localStorage.getItem(key); return value == null ? fallback : JSON.parse(value); }
      catch { return fallback; }
    },
    set(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch { } },
    raw(key, fallback) { try { const v = localStorage.getItem(key); return v == null ? fallback : v; } catch { return fallback; } },
    setRaw(key, value) { try { localStorage.setItem(key, value); } catch { } }
  };

  const demoMode = new URLSearchParams(location.search).has('demo');
  const platform = window.__JMDOWNLOAD_PLATFORM__ || 'web';
  const android = platform === 'android';

  /* 手机端外壳（Android 应用）暴露的原生动作。网页环境下是空操作。 */
  function nativeCall(action, params = {}) {
    if (!android) return false;
    const query = new URLSearchParams(params).toString();
    try { location.href = `jmd://${action}${query ? `?${query}` : ''}`; } catch { }
    return true;
  }

  /* 全屏铺满：界面一直画到屏幕物理边缘（连状态栏、手势条下面也是），
     系统栏高度由应用外壳实时同步过来，交给 CSS 自己去避开。
     网页版（含 iOS Safari 的刘海屏）继续用 env(safe-area-inset-*)。 */
  if (android) {
    const rootStyle = document.documentElement.style;
    window.__jmInsets = (left, top, right, bottom) => {
      rootStyle.setProperty('--safe-t', `${Number(top) || 0}px`);
      rootStyle.setProperty('--safe-b', `${Number(bottom) || 0}px`);
      rootStyle.setProperty('--safe-l', `${Number(left) || 0}px`);
      rootStyle.setProperty('--safe-r', `${Number(right) || 0}px`);
    };
    const initial = window.__JMDOWNLOAD_INSETS__;
    window.__jmInsets(initial?.left, initial?.top, initial?.right, initial?.bottom);
  }

  /* Android 上常见的手写漫画目录，作为路径快捷选项。 */
  const androidFolderSuggestions = [
    '/storage/emulated/0/JMDownload',
    '/storage/emulated/0/Download',
    '/storage/emulated/0/Pictures',
    '/storage/emulated/0/Documents'
  ];

  const defaultConfig = {
    base_dir: 'C:\\JMDownLoad', image_format: '.png', output_format: 'images', pdf_mode: 'merged',
    photo_threads: 1, image_threads: 5, album_threads: 1, filename_lang: 'traditional',
    auto_path: true, default_base_dir: 'C:\\JMDownLoad'
  };

  const searchSortLabels = { mr: '最新发布', mv: '浏览最多', mp: '页数最多', tf: '收藏最多' };
  const searchTimeLabels = { a: '全部时间', t: '今天', w: '本周', m: '本月' };
  const savedSearchSort = store.get('jm-search-sort-v1', 'mr');
  const savedSearchTime = store.get('jm-search-time-v1', 'a');

  const state = {
    view: 'discover', rank: 'day', mode: 'ranking', query: '', page: 1, hasNext: false,
    layout: store.get('jm-layout-v1', 'grid') === 'list' ? 'list' : 'grid',
    searchSort: searchSortLabels[savedSearchSort] ? savedSearchSort : 'mr',
    searchTime: searchTimeLabels[savedSearchTime] ? savedSearchTime : 'a',
    items: [], selected: new Map(), config: { ...defaultConfig },
    snapshot: { running: false, stopping: false, tasks: [], last_success_ids: [], last_failed_ids: [] },
    logs: store.get('jm-logs-v2', []), history: store.get('jm-history-v2', []), recent: store.get('jm-recent-v2', []),
    historyFilter: 'all', statFilter: 'all', saveTimer: 0, ws: null, reconnectTimer: 0, online: false,
    requestSerial: 0, tasksLoading: false, snapshotEventSerial: 0, resultController: null,
    detailSerial: 0, detailController: null, eventSequence: 0, historyTerminalCache: new Map(),
    searchMode: false, connected: false, wsBackoff: 2500
  };

  const renderCache = { selectionItems: null, taskLayout: null, logs: null, history: null, albums: null };
  const progressLogBuckets = new Map();
  const premiumState = { longPressAt: 0, contextMenu: null };

  /* ───────────────────────── 连接配置 ───────────────────────── */

  const conn = {
    base: store.raw('jm-mobile-base', '') || '',
    /* 应用外壳会把本地服务的令牌注入到 __JMDOWNLOAD_TOKEN__；
       浏览器里没有这个变量，就用手动填写的连接设置。 */
    token: store.raw('jm-mobile-token', '') || window.__JMDOWNLOAD_TOKEN__ || ''
  };

  const apiUrl = path => (conn.base ? conn.base.replace(/\/+$/, '') : '') + path;

  function setConnectionTarget(base, token) {
    conn.base = (base || '').trim().replace(/\/+$/, '');
    conn.token = (token || '').trim();
    store.setRaw('jm-mobile-base', conn.base);
    store.setRaw('jm-mobile-token', conn.token);
  }

  async function api(path, options = {}) {
    if (demoMode) return demoApi(path, options);
    const headers = new Headers(options.headers || {});
    if (conn.token) headers.set('Authorization', `Bearer ${conn.token}`);
    if (options.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
    const response = await fetch(apiUrl(path), { ...options, headers, credentials: 'omit' });
    const type = response.headers.get('content-type') || '';
    let payload = null;
    if (type.includes('application/json')) { try { payload = await response.json(); } catch { } }
    if (!response.ok) throw new Error(payload?.detail || `请求失败（${response.status}）`);
    return payload;
  }

  async function apiRaw(path, options = {}) {
    const headers = new Headers(options.headers || {});
    if (conn.token) headers.set('Authorization', `Bearer ${conn.token}`);
    return fetch(apiUrl(path), { ...options, headers, credentials: 'omit' });
  }

  /* 电脑端桥接的本地能力：目录浏览 / 在资源管理器中打开 */
  async function shellDirs(path) {
    if (demoMode) return { path: path || 'D:\\Library\\JMDownLoad', parent: 'D:\\Library', dirs: ['漫画', '2024', '收藏'] };
    const response = await apiRaw(`/api/shell/dirs?path=${encodeURIComponent(path || '')}`);
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(payload?.detail || '目录读取失败');
    return payload;
  }

  async function shellOpen(path) {
    if (demoMode) return true;
    const response = await apiRaw('/api/shell/open', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path: path || '' }) });
    if (!response.ok) { const payload = await response.json().catch(() => ({})); throw new Error(payload?.detail || '无法在电脑端打开目录'); }
    return true;
  }

  /* 演示模式没有真实封面，用一套固定的协调色板画渐变封面，界面才不会看起来是空的。 */
  const demoCoverPalette = [
    [160, 190], [198, 224], [30, 8], [268, 296], [118, 146], [332, 356], [78, 104], [242, 262]
  ];

  function demoCover(id) {
    const seed = [...String(id)].reduce((total, ch) => total + ch.charCodeAt(0), 0);
    const [hue, hue2] = demoCoverPalette[seed % demoCoverPalette.length];
    const svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 350 500">'
      + `<defs><linearGradient id="c" x1="0" y1="0" x2="1" y2="1">`
      + `<stop offset="0" stop-color="hsl(${hue},34%,72%)"/><stop offset="1" stop-color="hsl(${hue2},32%,48%)"/>`
      + '</linearGradient></defs>'
      + '<rect width="350" height="500" fill="url(#c)"/>'
      + `<circle cx="${72 + (seed % 180)}" cy="${118 + (seed % 150)}" r="112" fill="hsl(${hue2},38%,84%)" opacity=".38"/>`
      + '<rect x="34" y="336" width="282" height="126" rx="14" fill="#ffffff" opacity=".18"/>'
      + '<text x="175" y="292" text-anchor="middle" font-family="sans-serif" font-size="74" font-weight="700" fill="#ffffff" opacity=".72">J</text>'
      + '</svg>';
    return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
  }

  const coverUrl = id => demoMode ? demoCover(id) : apiUrl(`/api/cover/${encodeURIComponent(id)}${conn.token ? `?token=${encodeURIComponent(conn.token)}` : ''}`);

  /* ───────────────────────── 演示数据 ───────────────────────── */

  const demoAlbums = [
    ['438320', '夏日漫游指南', 1], ['422116', '午夜书店的来客', 2], ['397805', '和风小镇散步日记', 3],
    ['451928', '雨后的玻璃花房', 4], ['375214', '週末限定的秘密旅行', 5], ['469133', '琥珀色的午後时光', 6],
    ['408762', '城市边缘的观星者', 7], ['463501', '白昼梦与蓝色信箱', 8], ['349872', '沿海公路慢慢走', 9],
    ['470226', '风从庭院里经过', 10], ['389410', '咖啡冷掉以前', 11], ['456708', '昨日重现的唱片店', 12]
  ].map(([id, title, rank]) => ({ id, title, rank }));

  const demoChapters = albumId => Array.from({ length: 8 }, (_, i) => ({
    id: `${albumId}-c${i + 1}`,
    title: `第 ${i + 1} 话 · ${['初见的午后', '雨声与旧唱片', '沿海公路', '玻璃花房', '夜行列车的窗', '风从庭院经过', '漫长的夏天', '来信与回信'][i]}`,
    sort: i + 1
  }));

  function demoPageSvg(index, seed) {
    const hue = (seed * 37 + index * 23) % 360;
    return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 700 1000"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl(${hue},34%,86%)"/><stop offset="1" stop-color="hsl(${(hue + 40) % 360},30%,72%)"/></linearGradient></defs><rect width="700" height="1000" fill="url(#g)"/><circle cx="${120 + index * 40}" cy="${240 + index * 26}" r="150" fill="hsl(${(hue + 190) % 360},32%,80%)" opacity=".55"/><rect x="60" y="700" width="580" height="220" rx="18" fill="#ffffff" opacity=".5"/><text x="350" y="500" text-anchor="middle" font-family="sans-serif" font-size="64" fill="hsl(${hue},30%,32%)">第 ${index + 1} 页</text><text x="350" y="820" text-anchor="middle" font-family="sans-serif" font-size="30" fill="#666">演示模式 · 示例页面</text></svg>`;
  }

  let demoSession = null;

  async function demoApi(path, options = {}) {
    const wait = path.includes('tasks') ? 60 : 320;
    await new Promise(resolve => setTimeout(resolve, wait));
    const method = options.method || 'GET';

    if (path === '/api/session') return { authenticated: true, token_required: true };
    if (path === '/api/config') {
      if (method === 'PUT') { state.config = { ...state.config, ...JSON.parse(options.body || '{}') }; return state.config; }
      return { ...defaultConfig, base_dir: 'D:\\Library\\JMDownLoad', default_base_dir: 'D:\\Library\\JMDownLoad' };
    }
    if (path.startsWith('/api/ranking')) return { items: demoAlbums };
    if (path.startsWith('/api/search')) {
      const params = new URL(path, 'http://demo').searchParams;
      const q = params.get('q') || '', sort = params.get('sort') || 'mr';
      let items = demoAlbums.filter(x => x.title.includes(q) || x.id.includes(q)).map(x => ({ ...x, rank: null }));
      if (sort !== 'mr') items = [...items].reverse();
      return { items, page: 1, sort, time: params.get('time') || 'a', has_next: false };
    }
    if (path.startsWith('/api/album/')) {
      const id = decodeURIComponent(path.split('/').pop());
      const item = demoAlbums.find(x => x.id === id) || { id, title: `作品 ${id}` };
      return { ...item, author: '示例作者', tags: ['剧情', '日常', '彩色', '短篇'], page_count: 128 };
    }
    if (path === '/api/tasks' || path === '/health') return state.snapshot;
    if (path === '/api/download') {
      const body = JSON.parse(options.body || '{}');
      state.snapshot = {
        run_id: 'demo-run', running: true, stopping: false, current_item_id: body.ids[0], last_success_ids: [], last_failed_ids: [],
        tasks: body.ids.map((id, index) => ({ item_id: id, status: index === 0 ? 'running' : 'queued', base_dir: body.base_dir, message: index === 0 ? '正在准备章节信息' : '等待前序任务', progress: index === 0 ? 28 : 0, total: 100, total_known: true, detail: '' }))
      };
      setTimeout(() => renderSnapshot(state.snapshot), 60);
      return { started: true, ids: body.ids };
    }
    if (path === '/api/download/stop') { state.snapshot.running = false; state.snapshot.stopping = true; return { stopping: true }; }
    if (path.startsWith('/api/download/cancel/')) return { cancelled: true };
    if (path === '/api/download/reorder') return { reordered: true };

    /* 阅读器 */
    if (path === '/api/reader/settings') {
      if (method === 'PUT') return JSON.parse(options.body || '{}');
      return { mode: 'vertical', fit: 'comfortable', direction: 'ltr', prefetch: 3, cache_mb: 512, theme: 'system' };
    }
    if (path === '/api/reader/sessions' && method === 'POST') {
      const albumId = JSON.parse(options.body || '{}').album_id;
      demoSession = `demo-${albumId}`;
      const item = demoAlbums.find(x => x.id === albumId);
      return {
        session_id: demoSession,
        book: { id: albumId, title: item?.title || `作品 ${albumId}`, author: '示例作者', chapters: demoChapters(albumId), progress: null, favorite: false, local: false },
        bookmarks: [], settings: await demoApi('/api/reader/settings')
      };
    }
    const chapterMatch = path.match(/^\/api\/reader\/chapters\/([^?]+)/);
    if (chapterMatch) {
      const id = decodeURIComponent(chapterMatch[1]);
      const count = 10 + (id.charCodeAt(id.length - 1) % 6);
      return { id, album_id: id.split('-')[0], title: demoChapters(id.split('-')[0]).find(c => c.id === id)?.title || '章节', page_count: count, local: false, pages: Array.from({ length: count }, (_, i) => ({ index: i, url: `/api/reader/images/${demoSession}/${id}/${i}` })) };
    }
    if (path.startsWith('/api/reader/images/')) {
      const parts = path.split('/');
      const index = Number(parts.pop());
      const svg = demoPageSvg(index, parts[parts.length - 1].split('').reduce((a, c) => a + c.charCodeAt(0), 0));
      return svg;
    }
    if (path.startsWith('/api/reader/albums/')) {
      const id = decodeURIComponent(path.replace('/api/reader/albums/', ''));
      return { id, title: demoAlbums.find(x => x.id === id)?.title || `作品 ${id}`, chapters: demoChapters(id), progress: null, favorite: false, local: false };
    }
    if (path === '/api/reader/shelf') return { books: [], bookmarks: [] };
    if (path === '/api/reader/bookmarks') return { bookmarked: true };
    if (path === '/api/reader/cache') return { bytes: 0, budget_bytes: 512 * 1048576 };
    if (path.startsWith('/api/reader/progress/')) return JSON.parse(options.body || '{}');
    if (path.startsWith('/api/reader/favorites/')) return { favorite: true };
    if (path.startsWith('/api/shell/')) return {};
    return {};
  }

  function demoImageResponse(path) {
    const parts = path.split('/');
    const index = Number(parts.pop());
    const seed = parts[parts.length - 1].split('').reduce((a, c) => a + c.charCodeAt(0), 0);
    return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(demoPageSvg(index, seed))}`;
  }

  /* ───────────────────────── 主题 ───────────────────────── */

  function applyTheme(preference = store.get('jm-theme-v2', 'system')) {
    const dark = preference === 'dark' || (preference === 'system' && matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    $('#themeSelect').value = preference;
    store.set('jm-theme-v2', preference);
    window.JMReader?.themeChanged?.(preference);
    const meta = $('meta[name="theme-color"]:not([media])');
    if (meta) meta.setAttribute('content', dark ? '#111713' : '#f3f5f2');
    /* 让系统状态栏/导航栏跟着一起变，避免刘海区域和页面颜色对不上。 */
    nativeCall('theme', { dark: dark ? 1 : 0 });
  }

  function toggleTheme() { applyTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark'); }

  function applyMotion(preference = store.get('jm-motion-v1', 'full')) {
    const value = preference === 'reduced' ? 'reduced' : 'full';
    document.documentElement.dataset.motion = value;
    store.set('jm-motion-v1', value);
    const select = $('#motionSelect');
    if (select) select.value = value;
  }

  /* ───────────────────────── 吐司 ───────────────────────── */

  function toast(title, message = '', type = 'success', duration = 3000) {
    const node = document.createElement('div');
    node.className = `toast ${type}`;
    node.innerHTML = `<span class="toast-icon">${icon(type === 'error' ? 'x' : type === 'warning' ? 'info' : 'check')}</span><div><strong>${escapeHtml(title)}</strong>${message ? `<p>${escapeHtml(message)}</p>` : ''}</div>`;
    const region = $('#toastRegion');
    region.append(node);
    while (region.children.length > 3) region.firstElementChild.remove();
    const remove = () => { node.classList.add('is-leaving'); setTimeout(() => node.remove(), 200); };
    setTimeout(remove, duration);
  }

  /* ───────────────────────── 底部弹层 ───────────────────────── */

  const openSheets = [];
  const SHEET_CLOSE_MS = 430;

  /* 返回键的统一入口：先收浮层 → 退出搜索 → 回到「发现」 → 都没有才交给外壳退出应用。
     Android 由 Activity 调用 window.__jmBack，网页版挂在浏览器后退上。 */
  function goBack() {
    if (window.JMReader?.handleBack?.()) return true;
    if (closeTopSheet()) return true;
    if (state.searchMode) { exitSearch(true); return true; }
    if (state.view !== 'discover') { showView('discover'); return true; }
    return false;
  }
  window.__jmBack = () => (goBack() ? 'handled' : 'exit');
  if (!android) window.addEventListener('popstate', () => { goBack(); });

  function syncScrim() {
    const scrim = $('#scrim');
    if (openSheets.length && scrim.hidden) {
      scrim.hidden = false;
      requestAnimationFrame(() => scrim.classList.add('is-open'));
    } else if (!openSheets.length && !scrim.hidden) {
      scrim.classList.remove('is-open');
      setTimeout(() => { if (!openSheets.length) scrim.hidden = true; }, 300);
    }
  }

  function presentSheet(sheet) {
    sheet.hidden = false;
    sheet.classList.remove('is-dragging', 'is-settling');
    sheet.style.transform = '';
    void sheet.offsetHeight;
    sheet.classList.add('is-open');
  }

  function dismissSheet(sheet) {
    sheet.classList.remove('is-open');
    sheet.style.transform = '';
    setTimeout(() => { if (!sheet.classList.contains('is-open')) sheet.hidden = true; }, SHEET_CLOSE_MS);
  }

  function openSheet(sheet) {
    if (!sheet || openSheets.includes(sheet)) return;
    presentSheet(sheet);
    openSheets.push(sheet);
    syncScrim();
    syncFab();
  }

  function closeTopSheet() {
    const sheet = openSheets.pop();
    if (!sheet) return false;
    dismissSheet(sheet);
    syncScrim();
    syncFab();
    return true;
  }

  function closeAllSheets() {
    while (openSheets.length) dismissSheet(openSheets.pop());
    syncScrim();
    syncFab();
  }

  function bindSheetDrag(sheet) {
    const handle = $('.sheet-grip', sheet) || $('.sheet-head', sheet);
    if (!handle) return;
    let startY = 0, delta = 0, dragging = false, startTime = 0;
    const body = $('.sheet-body', sheet);

    handle.addEventListener('pointerdown', event => {
      if (event.target.closest('button, input, select, textarea, a')) return;
      if (body && body.scrollTop > 0) return;
      dragging = true; startY = event.clientY; delta = 0; startTime = performance.now();
      sheet.classList.add('is-dragging');
      handle.setPointerCapture?.(event.pointerId);
    });
    handle.addEventListener('pointermove', event => {
      if (!dragging) return;
      delta = Math.max(0, event.clientY - startY);
      sheet.style.transform = `translate3d(0, ${delta}px, 0)`;
    });
    const end = () => {
      if (!dragging) return;
      dragging = false;
      sheet.classList.remove('is-dragging');
      const speed = delta / Math.max(1, performance.now() - startTime);
      if (delta > sheet.offsetHeight * 0.28 || speed > 0.75) {
        sheet.style.transform = '';
        closeTopSheet();
      } else {
        sheet.style.transform = '';
        sheet.classList.add('is-settling');
      }
    };
    handle.addEventListener('pointerup', end);
    handle.addEventListener('pointercancel', end);
  }

  /* ───────────────────────── 视图切换 ───────────────────────── */

  function showView(view) {
    closeContextMenu();
    state.view = view;
    $$('.tab[data-view]').forEach(button => {
      const active = button.dataset.view === view;
      button.classList.toggle('is-active', active);
      if (active) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    });
    $$('.screen').forEach(node => node.classList.toggle('is-active', node.dataset.screen === view));
    if (view === 'queue') { renderSnapshot(state.snapshot); renderLogs(); loadTasks(); }
    if (view === 'shelf') window.JMReader?.loadShelf();
    if (view === 'history') renderHistory();
    syncQuickActions();
    syncFab();
  }

  function syncQuickActions() {
    const active = (state.snapshot.tasks || []).filter(task => task.status === 'running' || task.status === 'queued').length;
    const selected = state.selected.size;
    setText('#quickQueueMeta', active ? `${active} 个进行中` : '队列空闲');
    setText('#quickShelfMeta', window.JMReader ? '打开书架' : '浏览书架');
    const plan = $('#quickStrip');
    if (plan) plan.classList.toggle('has-selection', selected > 0);
  }

  function syncFab() {
    const fab = $('#floatingPlanButton');
    const count = state.selected.size;
    const readerOpen = !$('#readerWorkspace').hidden;
    fab.hidden = !(count > 0 && state.view !== 'queue' && openSheets.length === 0 && !readerOpen);
  }

  function currentScroll() { return $(`.screen[data-screen="${state.view}"] .screen-scroll`); }

  /* ───────────────────────── 发现 · 搜索与榜单 ───────────────────────── */

  function showSkeletons(count = 8) {
    $('#resultState').hidden = true;
    $('#albumGrid').innerHTML = Array.from({ length: count }, () => `<article class="skeleton-card"><div class="cover-wrap"></div><div class="skeleton-line"></div><div class="skeleton-line short"></div></article>`).join('');
  }

  function showResultState(title, message) {
    $('#albumGrid').innerHTML = '';
    const box = $('#resultState');
    box.innerHTML = `<strong>${escapeHtml(title)}</strong><p>${escapeHtml(message)}</p>`;
    box.hidden = false;
    $('#loadMoreWrap').hidden = true;
    renderCache.albums = null;
  }

  function syncSearchControls() {
    $$('#searchSortTabs button').forEach(button => button.classList.toggle('is-active', button.dataset.searchSort === state.searchSort));
    $('#searchTimeSelect').value = state.searchTime;
    $('#resetSearchFilters').hidden = state.searchSort === 'mr' && state.searchTime === 'a';
  }

  /* 搜索时把榜单整块让出来，只显示搜索栏 + 最近搜索；有结果后才换成结果列表。 */
  function syncSearchUi() {
    const screen = $('#screen-discover');
    const showingResults = state.searchMode && state.mode === 'search' && !!state.query;
    screen.classList.toggle('is-searching', state.searchMode);
    screen.classList.toggle('has-query', showingResults);
    const landing = $('#searchLanding');
    landing.hidden = !state.searchMode || showingResults;
    if (!landing.hidden) renderRecent();
  }

  function beginResults() {
    state.resultController?.abort();
    state.resultController = new AbortController();
    return { serial: ++state.requestSerial, signal: state.resultController.signal };
  }

  async function loadRanking(rank = state.rank) {
    const current = beginResults();
    const previous = state.mode === 'ranking' && state.rank === rank ? state.items : [];
    state.mode = 'ranking'; state.rank = rank; state.page = 1; state.query = '';
    $('#contentTitle').textContent = '热门榜单';
    syncSearchUi();
    $$('#rankingTabs button').forEach(b => b.classList.toggle('is-active', b.dataset.rank === rank));
    const cached = store.get(`jm-ranking-v3-${rank}`, null);
    const existing = previous.length ? previous : (cached?.items || []);
    state.hasNext = false;
    if (existing.length) { state.items = existing; renderAlbums(existing); } else showSkeletons();
    try {
      const data = await api(`/api/ranking?type=${encodeURIComponent(rank)}`, { signal: current.signal });
      if (current.serial !== state.requestSerial) return;
      state.items = data.items || [];
      renderAlbums(state.items);
      store.set(`jm-ranking-v3-${rank}`, { items: state.items, time: Date.now() });
      markConnected(true);
    } catch (error) {
      if (current.serial !== state.requestSerial || error.name === 'AbortError') return;
      markConnected(false, error.message);
      if (!existing.length) showResultState('榜单加载失败', error.message);
      toast('榜单暂时无法更新', error.message, 'warning');
    }
  }

  async function search(query, append = false) {
    query = String(query || '').trim();
    if (!query) { exitSearch(true); return; }
    const existing = state.mode === 'search' && state.query === query ? state.items : [];
    const current = beginResults(); const requestedPage = append ? state.page + 1 : 1;
    state.mode = 'search'; state.query = query;
    $('#contentTitle').textContent = `“${query}”`;
    syncSearchUi();
    $('#searchControls').hidden = false;
    syncSearchControls();
    if (!append && !existing.length) showSkeletons(6);
    $('#loadMoreButton').disabled = true;
    rememberSearch(query);
    try {
      const params = new URLSearchParams({ q: query, page: String(requestedPage), main_tag: '0', sort: state.searchSort, time: state.searchTime });
      const data = await api(`/api/search?${params}`, { signal: current.signal });
      if (current.serial !== state.requestSerial) return;
      const known = new Set(append ? state.items.map(i => String(i.id)) : []);
      const incoming = (data.items || []).filter(i => { const key = String(i.id); if (known.has(key)) return false; known.add(key); return true; });
      state.items = append ? [...state.items, ...incoming] : incoming;
      state.page = requestedPage;
      state.hasNext = !!data.has_next && (data.items || []).length > 0;
      if (append && incoming.length) renderAlbums(incoming, true);
      else if (!append) renderAlbums(state.items);
      $('#loadMoreWrap').hidden = !state.hasNext;
      markConnected(true);
    } catch (error) {
      if (current.serial !== state.requestSerial || error.name === 'AbortError') return;
      markConnected(false, error.message);
      if (!append && !existing.length) showResultState('没有拿到搜索结果', error.message);
      toast('搜索失败', error.message, 'error');
    } finally {
      if (current.serial === state.requestSerial) $('#loadMoreButton').disabled = false;
    }
  }

  function enterSearch() {
    if (state.searchMode) return;
    state.searchMode = true;
    $('#discoverBar').hidden = true;
    $('#searchBar').hidden = false;
    syncSearchUi();
    currentScroll()?.scrollTo({ top: 0 });
    setTimeout(() => $('#searchInput').focus(), 60);
  }

  function exitSearch(resetResults = true) {
    state.searchMode = false;
    $('#discoverBar').hidden = false;
    $('#searchBar').hidden = true;
    $('#searchInput').blur();
    $('#searchClear').hidden = true;
    $('#searchLanding').hidden = true;
    if (resetResults && state.mode === 'search') {
      state.query = '';
      $('#searchControls').hidden = true;
      $('#searchInput').value = '';
      loadRanking(state.rank);
    } else {
      syncSearchUi();
    }
  }

  function rememberSearch(query) {
    state.recent = [query, ...state.recent.filter(item => item !== query)].slice(0, 8);
    store.set('jm-recent-v2', state.recent);
    if (!$('#searchLanding').hidden) renderRecent();
  }

  function renderRecent() {
    $('#recentSearches').innerHTML = state.recent.length
      ? state.recent.map(query => `<button data-recent="${escapeHtml(query)}">${escapeHtml(query)}</button>`).join('')
      : '<div class="landing-empty">输入标题、作者或作品 ID 开始搜索</div>';
  }

  function syncLayout() {
    $('#albumGrid').classList.toggle('is-list', state.layout === 'list');
    $$('#layoutPicker [data-layout]').forEach(button => {
      const active = button.dataset.layout === state.layout;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-pressed', String(active));
    });
  }

  function albumMarkup(item, index) {
    const selected = state.selected.has(String(item.id));
    const src = coverUrl(item.id);
    const eager = index < 10;
    return `<article class="album-card" data-id="${escapeHtml(item.id)}" style="animation-delay:${Math.min(index, 10) * 24}ms">
      <div class="cover-wrap" data-detail="${escapeHtml(item.id)}">
        <span class="cover-placeholder">J</span>
        ${src ? `<img class="album-cover" src="${src}" alt="" loading="${eager ? 'eager' : 'lazy'}" fetchpriority="${index < 6 ? 'high' : 'auto'}" decoding="async">` : ''}
        ${item.rank ? `<span class="rank-badge ${item.rank <= 3 ? 'top' : ''}">#${item.rank}</span>` : ''}
        <span class="cover-id">JM ${escapeHtml(item.id)}</span>
        <button class="card-select ${selected ? 'is-selected' : ''}" data-toggle-select="${escapeHtml(item.id)}" data-selected="${selected ? '1' : '0'}" aria-label="${selected ? '移出' : '加入'}下载清单">${icon(selected ? 'check' : 'plus')}</button>
      </div>
      <div class="card-body">
        <h3 class="card-title" data-detail="${escapeHtml(item.id)}">${escapeHtml(item.title || `作品 ${item.id}`)}</h3>
        <div class="card-meta"><span>${item.rank ? `榜单第 ${item.rank} 名` : '搜索结果'}</span><button class="card-add ${selected ? 'is-on' : ''}" data-toggle-select="${escapeHtml(item.id)}">${selected ? '已加入' : '加入清单'}</button></div>
      </div>
    </article>`;
  }

  function renderAlbums(items, append = false) {
    $('#resultState').hidden = true;
    $('#loadMoreWrap').hidden = !(state.mode === 'search' && state.hasNext);
    if (!items.length) { showResultState('没有结果', '换个关键词，或用批量 ID 加入清单'); return; }
    const markup = items.map(albumMarkup).join('');
    if (append) $('#albumGrid').insertAdjacentHTML('beforeend', markup);
    else $('#albumGrid').innerHTML = markup;
    hydrateCovers($('#albumGrid'));
  }

  /* 封面淡入：图片解码完成后再显示，避免加载时的闪烁和布局抖动 */
  function hydrateCovers(root) {
    $$('img.album-cover', root).forEach(img => {
      if (img.dataset.bound) return;
      img.dataset.bound = '1';
      const done = () => img.classList.add('is-loaded');
      if (img.complete && img.naturalWidth) { done(); return; }
      img.addEventListener('load', done, { once: true });
      img.addEventListener('error', () => { img.remove(); }, { once: true });
    });
  }

  function syncAlbumSelection(id = null) {
    const targetId = id == null ? null : String(id);
    $$('.album-card').forEach(card => {
      if (targetId != null && card.dataset.id !== targetId) return;
      const selected = state.selected.has(String(card.dataset.id));
      const marker = selected ? '1' : '0';
      const selectButton = $('.card-select', card);
      if (selectButton && selectButton.dataset.selected !== marker) {
        selectButton.dataset.selected = marker;
        selectButton.classList.toggle('is-selected', selected);
        selectButton.setAttribute('aria-label', `${selected ? '移出' : '加入'}下载清单`);
        selectButton.innerHTML = icon(selected ? 'check' : 'plus');
      }
      const addButton = $('.card-add', card);
      if (addButton) {
        setText(addButton, selected ? '已加入' : '加入清单');
        addButton.classList.toggle('is-on', selected);
      }
    });
  }

  function toggleSelected(id, itemOverride) {
    id = String(id);
    if (state.selected.has(id)) {
      state.selected.delete(id);
      haptic(8);
    } else {
      const item = itemOverride || state.items.find(entry => String(entry.id) === id) || { id, title: `作品 ${id}` };
      state.selected.set(id, { id, title: item.title || `作品 ${id}` });
      haptic(12);
    }
    renderSelection();
    syncAlbumSelection(id);
  }

  function renderSelection() {
    const items = [...state.selected.values()];
    const count = items.length;
    setText('#selectedCount', count);
    setText('#plannerToggleCount', count);
    setText('#floatingPlanCount', count);
    $('#plannerToggleCount').hidden = count === 0;
    $('#selectionEmpty').hidden = count > 0;
    $('#selectedList').hidden = count === 0;
    $('#clearSelectedButton').hidden = count === 0;
    $('#startDownloadButton').disabled = count === 0 || state.snapshot.running;
    setText('#downloadHint', state.snapshot.running ? '当前有任务正在执行' : count ? `将下载 ${count} 个作品到所选目录` : '选择作品后即可开始');

    const itemKey = JSON.stringify(items.map(item => [String(item.id), item.title || `作品 ${item.id}`]));
    if (renderCache.selectionItems !== itemKey) {
      renderCache.selectionItems = itemKey;
      $('#selectedList').innerHTML = items.map(item => {
        const src = coverUrl(item.id);
        return `<div class="selected-item">
          ${src ? `<img class="selected-thumb" src="${src}" alt="" loading="lazy" decoding="async">` : '<span class="selected-thumb"></span>'}
          <div class="selected-copy"><strong>${escapeHtml(item.title)}</strong><small>JM ${escapeHtml(item.id)}</small></div>
          <button class="remove-selected" data-remove-selected="${escapeHtml(item.id)}" aria-label="移出清单">${icon('x')}</button>
        </div>`;
      }).join('');
    }
    syncFab();
  }

  function parseIds(text) {
    const source = String(text || '');
    const pattern = /(?:\b([pP])\s*|\b(?:JM)\s*|\/(photo|chapter|album)\/)?(\d{3,})\b/gi;
    const ids = [];
    for (const match of source.matchAll(pattern)) {
      const [, photoPrefix, route, digits] = match;
      const isPhoto = Boolean(photoPrefix) || /^(photo|chapter)$/i.test(route || '');
      ids.push(`${isPhoto ? 'p' : ''}${digits}`);
    }
    return [...new Set(ids)].slice(0, 200);
  }

  function updateBatchPreview() {
    const ids = parseIds($('#batchTextarea').value);
    $('#batchPreview').innerHTML = `<span>${ids.length ? `已识别：${ids.slice(0, 4).join('、')}${ids.length > 4 ? '…' : ''}` : '等待输入'}</span><strong>${ids.length} 个有效 ID</strong>`;
    $('#confirmBatchButton').disabled = ids.length === 0;
  }

  function openBatch() {
    $('#batchTextarea').value = '';
    updateBatchPreview();
    openSheet($('#batchSheet'));
    setTimeout(() => $('#batchTextarea').focus(), 380);
  }

  function confirmBatch() {
    const ids = parseIds($('#batchTextarea').value);
    let added = 0;
    ids.forEach(id => { if (!state.selected.has(id)) { state.selected.set(id, { id, title: `作品 ${id}` }); added++; } });
    renderSelection(); syncAlbumSelection();
    closeTopSheet();
    setTimeout(() => openSheet($('#plannerSheet')), 320);
    toast('已加入下载清单', `新增 ${added} 个，共 ${state.selected.size} 个作品`);
  }

  /* ───────────────────────── 作品详情 ───────────────────────── */

  async function showDetail(id) {
    state.detailController?.abort();
    state.detailController = new AbortController();
    const serial = ++state.detailSerial, signal = state.detailController.signal;
    const item = state.items.find(i => String(i.id) === String(id)) || state.selected.get(String(id));
    $('#detailContent').innerHTML = '<div class="detail-loading"><div class="spinner"></div><p>正在读取作品信息…</p></div>';
    openSheet($('#detailSheet'));
    try {
      const [detail, book] = await Promise.all([
        api(`/api/album/${encodeURIComponent(id)}`, { signal }),
        api(`/api/reader/albums/${encodeURIComponent(id)}`, { signal }).catch(() => null)
      ]);
      if (serial !== state.detailSerial || signal.aborted) return;
      const selected = state.selected.has(String(id));
      const src = coverUrl(id);
      const progress = book?.progress;
      const readLabel = progress ? `继续阅读 · 第 ${Number(progress.page) + 1} 页` : '开始阅读';
      $('#detailContent').innerHTML = `<div class="detail-hero">
          <div class="detail-cover-wrap">
            <span class="cover-placeholder">J</span>
            ${src ? `<img class="detail-cover" src="${src}" alt="">` : ''}
            <div class="detail-hero-plate">
              ${src ? `<img class="detail-hero-thumb" src="${src}" alt="">` : ''}
              <div class="detail-hero-copy">
                <span class="detail-overline">JM ${escapeHtml(detail.id || id)}</span>
                <h2 id="detailTitle">${escapeHtml(detail.title || item?.title || `作品 ${id}`)}</h2>
              </div>
            </div>
          </div>
          <div class="detail-info">
            <p class="detail-author">${escapeHtml(detail.author ? `作者：${detail.author}` : '作者信息暂缺')}</p>
            <div class="detail-tags">${(detail.tags || []).slice(0, 8).map(t => `<span>${escapeHtml(t)}</span>`).join('') || '<span>暂无标签</span>'}</div>
            <div class="detail-stats">
              <div><strong>${detail.page_count || '—'}</strong><small>页数</small></div>
              <div><strong>${book?.chapters?.length || '—'}</strong><small>章节</small></div>
            </div>
            <div class="detail-actions">
              <button class="primary-btn" id="detailReadButton">${icon('book')}${readLabel}</button>
              <button class="ghost-btn" id="detailSelectButton">${icon(selected ? 'check' : 'plus')}${selected ? '已在清单' : '加入清单'}</button>
            </div>
            ${progress ? '<div class="detail-read-secondary"><button id="detailRestartButton">从第一章开始</button></div>' : ''}
            ${book?.chapters?.length ? `<details class="detail-chapter-list"><summary aria-expanded="false"><span>章节目录 · ${book.chapters.length} 章</span><svg aria-hidden="true"><use href="#i-chevron"/></svg></summary><div class="detail-chapter-body" inert><div class="detail-chapter-items">${book.chapters.map(c => `<button data-detail-read-chapter="${escapeHtml(c.id)}"><span>${c.sort || ''}</span><b>${escapeHtml(c.title)}</b><svg aria-hidden="true"><use href="#i-chevron"/></svg></button>`).join('')}</div></div></details>` : ''}
          </div>
        </div>`;
      bindDetailChapters();
      hydrateCovers($('#detailContent'));
      $('#detailReadButton').onclick = () => window.JMReader.open(String(id));
      const restart = $('#detailRestartButton');
      if (restart) restart.onclick = () => window.JMReader.open(String(id), { fromStart: true });
      $$('#detailContent [data-detail-read-chapter]').forEach(b => b.onclick = () => window.JMReader.open(String(id), { chapter: b.dataset.detailReadChapter, page: 0 }));
      $('#detailSelectButton').onclick = () => {
        toggleSelected(id, { id, title: detail.title || item?.title });
        const active = state.selected.has(String(id));
        $('#detailSelectButton').innerHTML = `${icon(active ? 'check' : 'plus')}${active ? '已在清单' : '加入清单'}`;
      };
    } catch (error) {
      if (serial !== state.detailSerial || error.name === 'AbortError') return;
      $('#detailContent').innerHTML = `<div class="detail-loading">${icon('info')}<strong>详情读取失败</strong><p>${escapeHtml(error.message)}</p></div>`;
    }
  }

  function bindDetailChapters() {
    const details = $('#detailContent .detail-chapter-list');
    if (!details) return;
    const summary = $('summary', details), body = $('.detail-chapter-body', details), items = $('.detail-chapter-items', details);
    let expanded = false, animation = null, version = 0;
    summary.addEventListener('click', event => {
      event.preventDefault();
      const request = ++version, start = body.getBoundingClientRect().height;
      animation?.cancel();
      expanded = !expanded;
      details.open = true;
      details.dataset.expanded = String(expanded);
      summary.setAttribute('aria-expanded', String(expanded));
      body.inert = !expanded;
      const end = expanded ? items.getBoundingClientRect().height : 0;
      const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
      body.style.height = `${end}px`;
      const settle = () => {
        details.open = expanded;
        body.style.height = expanded ? 'auto' : '0px';
        animation = null;
      };
      if (reducedMotion || Math.abs(end - start) < 1) { settle(); return; }
      animation = body.animate([{ height: `${start}px` }, { height: `${end}px` }], { duration: 220, easing: 'cubic-bezier(.2,.7,.25,1)' });
      animation.finished.then(() => { if (request === version) settle(); }).catch(() => { });
    });
  }

  /* ───────────────────────── 配置 ───────────────────────── */

  function readConfigFromForm() {
    return {
      ...state.config,
      base_dir: $('#pathInput').value.trim() || state.config.default_base_dir || defaultConfig.base_dir,
      image_format: $('#imageFormatInput').value,
      output_format: $('#formatPicker .is-active')?.dataset.format || 'images',
      pdf_mode: $('#pdfModeInput').value,
      photo_threads: clamp($('#photoThreadsInput').value, 1, 5),
      image_threads: clamp($('#imageThreadsInput').value, 1, 20),
      album_threads: clamp($('#albumThreadsInput').value, 1, 8),
      filename_lang: $('#filenameLangInput').value,
      auto_path: $('#autoPathInput').checked
    };
  }

  function applyConfigToForm(config) {
    state.config = { ...defaultConfig, ...config };
    $('#pathInput').value = state.config.base_dir;
    $('#imageFormatInput').value = state.config.image_format;
    $('#pdfModeInput').value = state.config.pdf_mode;
    $('#photoThreadsInput').value = String(state.config.photo_threads);
    $('#imageThreadsInput').value = String(state.config.image_threads);
    $('#albumThreadsInput').value = String(state.config.album_threads);
    $('#filenameLangInput').value = state.config.filename_lang;
    $('#autoPathInput').checked = !!state.config.auto_path;
    $('#imageThreadsValue').value = state.config.image_threads;
    $('#albumThreadsValue').value = state.config.album_threads;
    $$('#formatPicker button').forEach(button => button.classList.toggle('is-active', button.dataset.format === state.config.output_format));
    $('#pdfModeRow').hidden = state.config.output_format !== 'pdf';
    setText('#settingsPathHint', state.config.base_dir || '下载目录保存在电脑端');
    setText('#plannerPathLabel', state.config.base_dir || defaultConfig.base_dir);
  }

  async function loadConfig() {
    try {
      applyConfigToForm(await api('/api/config'));
      setSaveState('saved', '设置已同步');
    } catch (error) {
      applyConfigToForm(defaultConfig);
      setSaveState('error', '设置读取失败');
    }
  }

  function setSaveState(mode, text) {
    const node = $('#saveState');
    node.classList.remove('is-saving', 'is-error');
    if (mode === 'saving') node.classList.add('is-saving');
    if (mode === 'error') node.classList.add('is-error');
    node.lastChild.textContent = text;
  }

  function queueConfigSave() {
    state.config = readConfigFromForm();
    setSaveState('saving', '正在保存…');
    clearTimeout(state.saveTimer);
    state.saveTimer = setTimeout(() => saveConfig(false).catch(() => { }), 450);
  }

  async function saveConfig(showFeedback = false) {
    clearTimeout(state.saveTimer);
    state.config = readConfigFromForm();
    try {
      const saved = await api('/api/config', { method: 'PUT', body: JSON.stringify(state.config) });
      applyConfigToForm(saved);
      setSaveState('saved', '设置已同步');
      if (showFeedback) toast('设置已保存');
      return saved;
    } catch (error) {
      setSaveState('error', '保存失败，稍后重试');
      if (showFeedback) toast('设置保存失败', error.message, 'error');
      throw error;
    }
  }

  async function openDirectory(path = $('#pathInput').value) {
    if (nativeCall('open-folder', { path: path || '' })) return;
    try { await shellOpen(path); toast('已在电脑端打开目录', path); }
    catch (error) { toast('无法打开目录', error.message, 'warning'); }
  }

  /* ───────────────────────── 电脑端目录选择器 ───────────────────────── */

  let folderTarget = 'pathInput';

  async function loadFolder(path) {
    const list = $('#folderList');
    list.innerHTML = `<div class="empty-state"><p>正在读取目录…</p></div>`;
    try {
      const data = await shellDirs(path);
      $('#folderPath').value = data.path || '';
      const dirs = data.dirs || [];
      list.innerHTML = dirs.length
        ? dirs.map(name => `<button class="folder-item" data-folder-enter="${escapeHtml(name)}">${icon('folder')}<span>${escapeHtml(name)}</span>${icon('chevron')}</button>`).join('')
        : `<div class="empty-state"><p>这个目录下没有子文件夹</p></div>`;
      $('#folderUp').disabled = !data.parent && !data.path;
    } catch (error) {
      if (android) { renderFolderSuggestions(); return; }
      list.innerHTML = `<div class="empty-state"><strong>目录读取失败</strong><p>${escapeHtml(error.message)}</p></div>`;
    }
  }

  /* 手机端没有电脑端那种目录浏览服务，改为直接输入路径 + 常用位置快捷选择。 */
  function renderFolderSuggestions() {
    if (!android) return;
    $('#folderUp').disabled = true;
    const current = $('#folderPath').value.trim();
    $('#folderList').innerHTML = androidFolderSuggestions
      .map(path => `<button class="folder-item ${path === current ? 'is-current' : ''}" data-folder-pick="${escapeHtml(path)}">${icon('folder')}<span>${escapeHtml(path)}</span>${icon('check')}</button>`)
      .join('');
  }

  function openFolderPicker(target) {
    folderTarget = target;
    openSheet($('#folderSheet'));
    if (android) {
      if (!$('#folderPath').value.trim()) $('#folderPath').value = $('#pathInput').value.trim();
      renderFolderSuggestions();
      return;
    }
    loadFolder($('#pathInput').value);
  }

  function applyFolderPick() {
    const path = $('#folderPath').value.trim();
    if (!path) return;
    $('#pathInput').value = path;
    setText('#settingsPathHint', path);
    queueConfigSave();
    toast('保存位置已更新', path);
    closeTopSheet();
  }

  /* ───────────────────────── 下载 ───────────────────────── */

  async function startDownload() {
    const ids = [...state.selected.keys()];
    if (!ids.length || state.snapshot.running) return;
    const button = $('#startDownloadButton');
    button.disabled = true;
    button.querySelector('b').textContent = '正在创建任务…';
    try {
      const config = await saveConfig(false);
      await api('/api/download', { method: 'POST', body: JSON.stringify({ ...config, ids }) });
      ids.forEach(id => upsertHistory({ id, title: state.selected.get(id)?.title || `作品 ${id}`, status: 'running', path: config.base_dir, time: new Date().toISOString() }));
      state.selected.clear();
      renderSelection();
      syncAlbumSelection();
      closeTopSheet();
      showView('queue');
      await loadTasks();
      toast('任务已加入队列', `共 ${ids.length} 个作品，下载会在后台持续进行`);
    } catch (error) {
      toast('任务创建失败', error.message, 'error');
      button.disabled = false;
    } finally {
      button.querySelector('b').textContent = '开始下载';
    }
  }

  async function loadTasks() {
    if (state.tasksLoading) return;
    state.tasksLoading = true;
    const eventSerial = state.snapshotEventSerial;
    try {
      const snapshot = await api('/api/tasks');
      if (eventSerial !== state.snapshotEventSerial) return;
      renderSnapshot(snapshot);
      markConnected(true);
    } catch (error) {
      markConnected(false, error.message);
    } finally {
      state.tasksLoading = false;
    }
  }

  function renderSnapshot(snapshot) {
    if (snapshot.run_id && snapshot.run_id !== state.snapshot.run_id) state.historyTerminalCache.clear();
    state.snapshot = { running: false, stopping: false, tasks: [], last_success_ids: [], last_failed_ids: [], ...snapshot };
    const tasks = state.snapshot.tasks || [];
    const success = tasks.filter(task => successStatus(task.status)).length;
    const failed = tasks.filter(task => task.status === 'failed' || task.status === 'cancelled').length;
    const running = tasks.filter(task => task.status === 'running').length;
    setText('#statTotal', tasks.length);
    setText('#statRunning', running);
    setText('#statSuccess', success);
    setText('#statFailed', failed);
    setText('#runningName', state.snapshot.current_item_id ? `JM ${state.snapshot.current_item_id}` : '暂无任务');
    setText('#queueSummary', tasks.length ? `${success + failed} / ${tasks.length} 个任务已处理` : '队列目前为空');
    const pill = $('#queueStatePill');
    pill.className = `status-pill${state.snapshot.stopping ? ' is-warning' : state.snapshot.running ? ' is-running' : ''}`;
    setText(pill, state.snapshot.stopping ? '正在停止' : state.snapshot.running ? '运行中' : '空闲');
    $('#stopAllButton').disabled = !state.snapshot.running || state.snapshot.stopping;
    $('#queueBadge').hidden = !state.snapshot.running;
    syncQuickActions();
    renderSelection();
    if (state.view === 'queue') renderTasks(tasks);
    syncHistoryFromSnapshot();
  }

  function taskPercent(task) {
    if (successStatus(task.status)) return 100;
    if (task.total_known === false) return 0;
    if (Number(task.total) > 0) return clamp(Math.round(Number(task.progress || 0) / Number(task.total) * 100), 0, 100);
    return 0;
  }

  function taskProgressLabel(task) {
    if (task.total_known === false && !successStatus(task.status)) return `已完成 ${Number(task.progress || 0)} 页`;
    return `${taskPercent(task)}%`;
  }

  function taskLayoutKey(tasks) {
    if (!tasks.length) return '__empty__';
    return tasks.map((task, index) => [String(task.item_id), String(task.status || 'queued'), String(task.base_dir || ''), index, tasks.length].join('\u0000')).join('\u0001');
  }

  function taskMarkup(task, index, totalTasks) {
    const status = task.status || 'queued';
    const percent = taskPercent(task);
    const statusIcon = successStatus(status) ? 'check' : status === 'failed' || status === 'cancelled' ? 'x' : status === 'running' ? 'download' : 'clock';
    const queued = status === 'queued';
    const active = queued || status === 'running';
    const message = task.message || task.detail || '等待处理';
    return `<article class="task-item is-${escapeHtml(status)}" data-task="${escapeHtml(task.item_id)}" data-total-known="${task.total_known === false ? '0' : '1'}">
      <span class="task-status-icon">${icon(statusIcon)}</span>
      <div class="task-main">
        <div class="task-title-row"><strong>JM ${escapeHtml(task.item_id)}</strong><span>${statusLabel(status)} · ${taskProgressLabel(task)}</span></div>
        <p>${escapeHtml(message)}</p>
        <div class="progress-track"><i style="width:${percent}%"></i></div>
      </div>
      <div class="task-actions">
        ${queued ? `<button data-reorder="-1" data-id="${escapeHtml(task.item_id)}" aria-label="上移" ${index === 0 ? 'disabled' : ''}>${icon('arrow-up')}</button><button data-reorder="1" data-id="${escapeHtml(task.item_id)}" aria-label="下移" ${index === totalTasks - 1 ? 'disabled' : ''}>${icon('arrow-down')}</button>` : ''}
        ${successStatus(status) ? `<button data-open-task="${escapeHtml(task.base_dir)}" aria-label="打开目录">${icon('folder')}</button>` : ''}
        ${active ? `<button class="danger" data-cancel-task="${escapeHtml(task.item_id)}" data-base-dir="${escapeHtml(task.base_dir)}" aria-label="取消任务">${icon('x')}</button>` : ''}
      </div>
    </article>`;
  }

  function renderTasks(tasks) {
    const filtered = state.statFilter === 'all' ? tasks
      : state.statFilter === 'running' ? tasks.filter(t => t.status === 'running' || t.status === 'queued')
        : state.statFilter === 'success' ? tasks.filter(t => successStatus(t.status))
          : tasks.filter(t => t.status === 'failed' || t.status === 'cancelled');
    const layoutKey = taskLayoutKey(filtered) + '|' + state.statFilter;
    const list = $('#taskList');
    if (!filtered.length) {
      if (renderCache.taskLayout !== layoutKey) {
        renderCache.taskLayout = layoutKey;
        list.innerHTML = `<div class="empty-state"><strong>${tasks.length ? '这个筛选下没有任务' : '队列为空'}</strong><p>${tasks.length ? '点上方统计切换筛选' : '在「发现」里加入作品后开始下载'}</p></div>`;
      }
      return;
    }
    if (renderCache.taskLayout !== layoutKey) {
      list.innerHTML = filtered.map((task, index) => taskMarkup(task, index, filtered.length)).join('');
      renderCache.taskLayout = layoutKey;
      return;
    }
    const nodes = new Map($$('[data-task]', list).map(node => [String(node.dataset.task), node]));
    filtered.forEach(task => {
      const node = nodes.get(String(task.item_id));
      if (!node) return;
      const status = task.status || 'queued';
      const percent = taskPercent(task);
      const message = task.message || task.detail || '等待处理';
      node.dataset.totalKnown = task.total_known === false ? '0' : '1';
      setText($('.task-title-row span', node), `${statusLabel(status)} · ${taskProgressLabel(task)}`);
      setText($('.task-main p', node), message);
      const bar = $('.progress-track i', node);
      const width = `${percent}%`;
      if (bar && bar.style.width !== width) bar.style.width = width;
    });
  }

  async function stopAll() {
    try {
      $('#stopAllButton').disabled = true;
      await api('/api/download/stop', { method: 'POST', body: '{}' });
      state.snapshot.stopping = true;
      renderSnapshot(state.snapshot);
      addLog({ level: 'WARNING', message: '已请求停止全部任务，当前请求结束后会退出。' });
      toast('正在停止任务', '已经提交停止请求', 'warning');
    } catch (error) { toast('停止失败', error.message, 'error'); }
  }

  async function cancelTask(id, baseDir) {
    try {
      const result = await api(`/api/download/cancel/${encodeURIComponent(id)}`, { method: 'POST', body: JSON.stringify({ base_dir: baseDir, output_format: state.config.output_format }) });
      if (result.cancelled) { toast(`已取消 JM ${id}`, '', 'warning'); await loadTasks(); }
      else toast('任务状态已变化', '这个任务可能已经结束', 'warning');
    } catch (error) { toast('取消失败', error.message, 'error'); }
  }

  async function reorderTask(id, direction) {
    try {
      const result = await api('/api/download/reorder', { method: 'POST', body: JSON.stringify({ item_id: id, direction: Number(direction) }) });
      if (result.reordered) await loadTasks();
    } catch (error) { toast('调整顺序失败', error.message, 'error'); }
  }

  /* ───────────────────────── 活动记录 ───────────────────────── */

  function shouldRecordLog(event) {
    const itemId = event.item_id == null ? '' : String(event.item_id);
    if (event.type !== 'item_progress') {
      if (itemId && ['item_success', 'item_failed', 'item_cancelled'].includes(event.type)) progressLogBuckets.delete(itemId);
      return true;
    }
    const data = event.data || {};
    const progress = Number(data.progress || 0), total = Number(data.total || 0), stage = String(data.stage || 'progress');
    const bucket = total > 0 ? Math.min(10, Math.floor(progress / total * 10)) : stage;
    const signature = `${stage}:${bucket}`;
    if (progressLogBuckets.get(itemId) === signature) return false;
    progressLogBuckets.set(itemId, signature);
    return true;
  }

  function addLog(event) {
    if (!shouldRecordLog(event)) return;
    const entry = { type: event.type || '', level: event.level || 'INFO', message: event.message || '任务状态已更新', item_id: event.item_id || null, time: new Date().toISOString() };
    state.logs = [entry, ...state.logs].slice(0, 80);
    store.set('jm-logs-v2', state.logs);
    if (state.view === 'queue') renderLogs();
  }

  function renderLogs() {
    const logKey = JSON.stringify(state.logs.map(entry => [entry.level, entry.message, entry.item_id, entry.time]));
    if (renderCache.logs === logKey) return;
    renderCache.logs = logKey;
    if (!state.logs.length) {
      $('#logList').innerHTML = `<div class="empty-state"><strong>暂无活动</strong><p>下载开始后这里会记录关键事件</p></div>`;
      return;
    }
    $('#logList').innerHTML = state.logs.map(entry => `<div class="log-entry ${String(entry.level).toLowerCase()}"><i class="log-dot"></i><div><p>${escapeHtml(entry.message)}</p><time>${formatTime(entry.time)}${entry.item_id ? ` · JM ${escapeHtml(entry.item_id)}` : ''}</time></div></div>`).join('');
  }

  /* ───────────────────────── 下载记录 ───────────────────────── */

  function upsertHistory(entry) {
    const index = state.history.findIndex(item => String(item.id) === String(entry.id));
    if (index >= 0) {
      const current = state.history[index];
      const next = { ...current, ...entry };
      const unchanged = ['id', 'title', 'status', 'path', 'time'].every(key => String(current[key] ?? '') === String(next[key] ?? ''));
      if (unchanged) return false;
      state.history[index] = next;
    } else state.history.unshift(entry);
    state.history = state.history.slice(0, 150).sort((a, b) => new Date(b.time) - new Date(a.time));
    store.set('jm-history-v2', state.history);
    if (state.view === 'history') renderHistory();
    return true;
  }

  function syncHistoryFromSnapshot() {
    const successIds = new Set((state.snapshot.last_success_ids || []).map(String));
    const failedIds = new Set((state.snapshot.last_failed_ids || []).map(String));
    (state.snapshot.tasks || []).forEach(task => {
      const status = successIds.has(String(task.item_id)) || successStatus(task.status) ? 'success'
        : failedIds.has(String(task.item_id)) || task.status === 'failed' || task.status === 'cancelled' ? 'failed' : task.status;
      if (status === 'success' || status === 'failed') {
        const signature = `${status}|${task.base_dir || ''}`;
        if (state.historyTerminalCache.get(String(task.item_id)) === signature) return;
        state.historyTerminalCache.set(String(task.item_id), signature);
        const old = state.history.find(item => String(item.id) === String(task.item_id));
        upsertHistory({ id: String(task.item_id), title: old?.title || `作品 ${task.item_id}`, status, path: task.base_dir || old?.path || state.config.base_dir, time: old?.status === status ? old.time : new Date().toISOString() });
      } else state.historyTerminalCache.delete(String(task.item_id));
    });
  }

  function renderHistory() {
    const items = state.history.filter(item => state.historyFilter === 'all' || item.status === state.historyFilter);
    const historyKey = JSON.stringify([state.historyFilter, items.map(item => [item.id, item.title, item.status, item.path, item.time])]);
    if (renderCache.history === historyKey) return;
    renderCache.history = historyKey;
    if (!items.length) {
      $('#historyList').innerHTML = `<div class="empty-state"><strong>${state.history.length ? '这个筛选下没有记录' : '还没有下载记录'}</strong><p>${state.history.length ? '切到「全部」看看' : '完成一次下载后会出现在这里'}</p></div>`;
      return;
    }
    $('#historyList').innerHTML = items.map(item => {
      const failed = item.status === 'failed';
      return `<article class="history-item ${failed ? 'failed' : ''}">
        <span class="history-icon">${icon(failed ? 'x' : item.status === 'running' ? 'download' : 'check')}</span>
        <div class="history-copy"><strong>${escapeHtml(item.title || `作品 ${item.id}`)}</strong><p>JM ${escapeHtml(item.id)} · ${escapeHtml(item.path || '保存目录未知')}</p></div>
        <div class="history-meta"><time>${formatTime(item.time)}</time><div><button data-history-add="${escapeHtml(item.id)}" data-title="${escapeHtml(item.title || '')}">再下载</button><button data-history-open="${escapeHtml(item.path || '')}">打开</button></div></div>
      </article>`;
    }).join('');
  }

  /* ───────────────────────── 实时事件 ───────────────────────── */

  function handleDownloadEvent(event) {
    const data = event.data || {};
    if (data.sequence && data.sequence <= state.eventSequence) return;
    if (data.sequence) state.eventSequence = data.sequence;
    if (event.type === 'tasks_delta') {
      if (data.run_id && state.snapshot.run_id && data.run_id !== state.snapshot.run_id) return;
      const changed = new Map((data.changed_tasks || []).map(t => [String(t.item_id), t]));
      state.snapshotEventSerial++;
      renderSnapshot({ ...state.snapshot, running: data.running, stopping: data.stopping, tasks: state.snapshot.tasks.map(t => changed.get(String(t.item_id)) || t) });
      return;
    }
    addLog(event);
    const snapshot = event.data?.snapshot || event.snapshot;
    if (snapshot) { state.snapshotEventSerial++; renderSnapshot(snapshot); }
    else loadTasks();
    if (event.type === 'item_success') toast('下载完成', event.message || `JM ${event.item_id} 已保存`);
    if (event.type === 'item_failed') toast('任务出现异常', event.message || `JM ${event.item_id} 下载失败`, 'error', 4600);
    if (event.type === 'finished') toast('本轮任务已结束', event.message || '所有队列任务都已处理');
  }

  function connectWebSocket() {
    clearTimeout(state.reconnectTimer);
    if (demoMode) { markConnected(true, '演示模式'); return; }
    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
    const base = conn.base ? conn.base.replace(/^http/, 'ws') : `${scheme}://${location.host}`;
    try {
      const ws = new WebSocket(`${base}/ws/events${conn.token ? `?token=${encodeURIComponent(conn.token)}` : ''}`);
      state.ws = ws;
      ws.addEventListener('open', () => { state.wsBackoff = 2500; markConnected(true); loadTasks(); });
      ws.addEventListener('message', message => { try { handleDownloadEvent(JSON.parse(message.data)); } catch { } });
      ws.addEventListener('close', () => {
        if (state.ws !== ws) return;
        state.ws = null;
        state.reconnectTimer = setTimeout(connectWebSocket, state.wsBackoff);
        state.wsBackoff = Math.min(20000, state.wsBackoff * 1.7);
      });
      ws.addEventListener('error', () => ws.close());
    } catch {
      state.reconnectTimer = setTimeout(connectWebSocket, state.wsBackoff);
      state.wsBackoff = Math.min(20000, state.wsBackoff * 1.7);
    }
  }

  function markConnected(online, detail) {
    state.connected = online;
    const pill = $('#serverStatusPill');
    if (pill) {
      pill.className = `status-pill ${online ? 'is-online' : 'is-offline'}`;
      setText(pill, online ? '已连接' : '未连接');
    }
    if (detail) setText('#serverStatusDetail', detail);
    else if (online) setText('#serverStatusDetail', demoMode ? '演示模式：界面完整可用，不连接电脑端' : android ? '应用内置服务运行正常' : '与电脑端服务通信正常');
    syncOfflineBanner();
    syncStorageBanner();
  }

  let bannerDismissed = false;
  function syncOfflineBanner() {
    if (state.connected || bannerDismissed || demoMode) {
      $('#offlineBanner')?.remove();
      return;
    }
    if ($('#offlineBanner')) return;
    const node = document.createElement('div');
    node.id = 'offlineBanner';
    node.className = 'result-state';
    node.innerHTML = android
      ? `<strong>本地服务启动失败</strong><p>完全退出应用后重新打开试试</p>
         <div class="detail-actions" style="justify-content:center"><button class="ghost-btn" id="bannerRetry">重新加载</button><button class="ghost-btn" id="bannerDemo">演示模式</button></div>`
      : `<strong>还没有连接到服务</strong><p>到「设置 → 连接」填写服务地址</p>
         <div class="detail-actions" style="justify-content:center"><button class="ghost-btn" id="bannerRetry">重新加载</button><button class="ghost-btn" id="bannerDemo">演示模式</button></div>`;
    const scroll = $('#discoverScroll');
    const anchor = $('.search-entry', scroll);
    if (anchor?.nextElementSibling) scroll.insertBefore(node, anchor.nextElementSibling);
    else scroll.insertBefore(node, scroll.firstElementChild);
    $('#bannerDemo').onclick = () => { location.search = '?demo'; };
    $('#bannerRetry').onclick = () => location.reload();
  }

  /* Android 上要保存到公共目录，需要「所有文件访问」权限，这里给一次明确的引导。 */
  function syncStorageBanner() {
    if (!android || window.__JMDOWNLOAD_STORAGE__ !== false) { $('#storageBanner')?.remove(); return; }
    if ($('#storageBanner') || !state.connected) return;
    const node = document.createElement('div');
    node.id = 'storageBanner';
    node.className = 'result-state';
    node.innerHTML = `<strong>需要存储权限</strong><p>否则漫画只能存到应用私有目录</p>
      <div class="detail-actions" style="justify-content:center"><button class="ghost-btn" id="storageGrant">去授权</button></div>`;
    const scroll = $('#discoverScroll');
    const anchor = $('.search-entry', scroll);
    if (anchor?.nextElementSibling) scroll.insertBefore(node, anchor.nextElementSibling);
    else scroll.insertBefore(node, scroll.firstElementChild);
    $('#storageGrant').onclick = () => nativeCall('request-storage');
  }

  /* ───────────────────────── 下拉刷新 ───────────────────────── */

  function attachPullToRefresh(scroller, onRefresh) {
    if (!scroller) return;
    const indicator = document.createElement('div');
    indicator.className = 'pull-indicator';
    indicator.innerHTML = `<span class="pull-spinner"></span>`;
    scroller.parentElement.insertBefore(indicator, scroller);

    let startY = 0, pulling = false, distance = 0, active = false;
    const threshold = 66;

    scroller.addEventListener('touchstart', event => {
      if (scroller.scrollTop > 0 || event.touches.length !== 1) { active = false; return; }
      active = true; pulling = false; distance = 0; startY = event.touches[0].clientY;
    }, { passive: true });

    scroller.addEventListener('touchmove', event => {
      if (!active) return;
      const dy = event.touches[0].clientY - startY;
      if (dy <= 0) { if (pulling) reset(); return; }
      if (scroller.scrollTop > 0) { reset(); return; }
      pulling = true;
      distance = Math.min(dy * 0.52, 110);
      indicator.style.height = `${distance}px`;
      indicator.style.opacity = String(Math.min(1, distance / threshold));
      indicator.classList.toggle('is-ready', distance >= threshold);
    }, { passive: true });

    const finish = async () => {
      if (!pulling) { active = false; return; }
      const fire = distance >= threshold;
      reset();
      active = false;
      if (fire) {
        indicator.classList.add('is-loading');
        indicator.style.height = '48px';
        indicator.style.opacity = '1';
        try { await onRefresh(); } finally {
          indicator.classList.remove('is-loading', 'is-ready');
          indicator.style.height = '0px';
          indicator.style.opacity = '0';
        }
      }
    };
    const reset = () => {
      pulling = false;
      indicator.style.height = '0px';
      indicator.style.opacity = '0';
      indicator.classList.remove('is-ready');
    };
    scroller.addEventListener('touchend', finish, { passive: true });
    scroller.addEventListener('touchcancel', () => { reset(); active = false; }, { passive: true });
  }

  /* ───────────────────────── 高级交互 ───────────────────────── */

  function closeContextMenu() {
    const menu = premiumState.contextMenu;
    if (!menu) return;
    menu.classList.remove('is-open');
    setTimeout(() => menu.remove(), 180);
    premiumState.contextMenu = null;
  }

  function showContextMenu(title, actions, x, y) {
    closeContextMenu();
    const menu = document.createElement('div');
    menu.className = 'context-menu';
    menu.setAttribute('role', 'menu');
    menu.innerHTML = `<div class="context-menu-title">${escapeHtml(title)}</div><div class="context-menu-actions">${actions.map((action, index) => `<button type="button" role="menuitem" data-context-action="${index}" class="${action.danger ? 'is-danger' : ''}">${icon(action.icon || 'spark')}<span>${escapeHtml(action.label)}</span></button>`).join('')}</div>`;
    document.body.append(menu);
    premiumState.contextMenu = menu;
    const left = clamp(Number(x) || innerWidth / 2, 16, innerWidth - 16);
    const top = clamp(Number(y) || innerHeight / 2, 16, innerHeight - 16);
    menu.style.left = `${left}px`;
    menu.style.top = `${top}px`;
    requestAnimationFrame(() => {
      const rect = menu.getBoundingClientRect();
      menu.style.left = `${clamp(left - rect.width / 2, 12, innerWidth - rect.width - 12)}px`;
      menu.style.top = `${clamp(top - rect.height - 12, 12 + (Number.parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--safe-t')) || 0), innerHeight - rect.height - 12)}px`;
      menu.classList.add('is-open');
    });
    menu.addEventListener('click', event => {
      const button = event.target.closest('[data-context-action]');
      if (!button) return;
      const action = actions[Number(button.dataset.contextAction)];
      closeContextMenu();
      action?.run?.();
    });
    setTimeout(() => document.addEventListener('pointerdown', event => {
      if (premiumState.contextMenu && !premiumState.contextMenu.contains(event.target)) closeContextMenu();
    }, { once: true }), 0);
  }

  function bindLongPress(container, selector, menuFactory) {
    if (!container) return;
    let timer = 0, node = null, startX = 0, startY = 0, triggered = false;
    const clear = () => { if (timer) clearTimeout(timer); timer = 0; node?.classList.remove('is-long-pressed'); node = null; };
    container.addEventListener('pointerdown', event => {
      if (event.pointerType === 'mouse' && event.button !== 0) return;
      const target = event.target.closest(selector);
      if (!target || !container.contains(target) || event.target.closest('button, a, input, select, textarea')) return;
      node = target; startX = event.clientX; startY = event.clientY; triggered = false;
      timer = window.setTimeout(() => {
        if (!node) return;
        triggered = true; premiumState.longPressAt = performance.now();
        document.body.dataset.jmLongPress = '1';
        window.setTimeout(() => { delete document.body.dataset.jmLongPress; }, 850);
        node.classList.add('is-long-pressed');
        haptic(18);
        const data = menuFactory(node, event);
        if (data) showContextMenu(data.title, data.actions, event.clientX, event.clientY);
      }, 520);
    });
    container.addEventListener('pointermove', event => {
      if (!node || Math.hypot(event.clientX - startX, event.clientY - startY) > 12) clear();
    });
    container.addEventListener('pointerup', () => { if (!triggered) clear(); else setTimeout(clear, 0); });
    container.addEventListener('pointercancel', clear);
  }

  function bindSwipeActions(container, selector, resolve) {
    if (!container) return;
    let gesture = null;
    const reset = () => {
      if (!gesture) return;
      gesture.node.classList.remove('is-swipe-tracking');
      gesture.node.style.transform = '';
      gesture = null;
    };
    container.addEventListener('pointerdown', event => {
      if (event.pointerType === 'mouse' && event.button !== 0) return;
      const node = event.target.closest(selector);
      if (!node || !container.contains(node) || event.target.closest('button, a, input, select, textarea')) return;
      gesture = { node, startX: event.clientX, startY: event.clientY, locked: false, dx: 0 };
    });
    container.addEventListener('pointermove', event => {
      if (!gesture) return;
      gesture.dx = event.clientX - gesture.startX;
      const dy = event.clientY - gesture.startY;
      if (!gesture.locked && Math.abs(gesture.dx) > 12 && Math.abs(gesture.dx) > Math.abs(dy) * 1.15) {
        gesture.locked = true;
        gesture.node.classList.add('is-swipe-tracking');
        try { container.setPointerCapture(event.pointerId); } catch { }
      }
      if (!gesture.locked) return;
      event.preventDefault();
      const distance = clamp(gesture.dx, -92, 92);
      gesture.node.style.transform = `translate3d(${distance}px, 0, 0)`;
    });
    container.addEventListener('pointerup', event => {
      if (!gesture) return;
      const current = gesture;
      if (current.locked && Math.abs(current.dx) > 62) {
        reset();
        resolve(current.node, current.dx < 0 ? 'left' : 'right');
        haptic(12);
      } else reset();
      try { container.releasePointerCapture(event.pointerId); } catch { }
    });
    container.addEventListener('pointercancel', reset);
  }

  function bindPremiumInteractions() {
    bindLongPress($('#albumGrid'), '.album-card', card => {
      const id = String(card.dataset.id);
      const item = state.items.find(entry => String(entry.id) === id);
      const selected = state.selected.has(id);
      return { title: item?.title || `JM ${id}`, actions: [
        { label: '打开详情', icon: 'info', run: () => showDetail(id) },
        { label: selected ? '移出下载清单' : '加入下载清单', icon: selected ? 'x' : 'plus', run: () => toggleSelected(id, item) },
        { label: '开始阅读', icon: 'book', run: () => window.JMReader?.open(id) }
      ] };
    });
    bindLongPress($('#taskList'), '.task-item', card => {
      const task = (state.snapshot.tasks || []).find(entry => String(entry.item_id) === String(card.dataset.task));
      if (!task) return null;
      const actions = [];
      if (task.status === 'queued') actions.push({ label: '移到队首', icon: 'arrow-up', run: () => reorderTask(task.item_id, -1) });
      if (successStatus(task.status)) actions.push({ label: '打开目录', icon: 'folder', run: () => openDirectory(task.base_dir) });
      if (task.status === 'running' || task.status === 'queued') actions.push({ label: '取消任务', icon: 'x', danger: true, run: () => cancelTask(task.item_id, task.base_dir) });
      return { title: `JM ${task.item_id}`, actions };
    });
    bindLongPress($('#shelfList'), '.shelf-item', card => {
      const id = String(card.dataset.shelfItem || '');
      if (!id) return null;
      return { title: card.querySelector('.shelf-copy strong')?.textContent || `JM ${id}`, actions: [
        { label: '继续阅读', icon: 'book', run: () => card.querySelector('[data-shelf-open]')?.click() },
        { label: '移出书架', icon: 'x', danger: true, run: () => card.querySelector('[data-shelf-remove]')?.click() }
      ] };
    });
    bindSwipeActions($('#taskList'), '.task-item', (card, direction) => {
      const task = (state.snapshot.tasks || []).find(entry => String(entry.item_id) === String(card.dataset.task));
      if (!task) return;
      if (direction === 'left' && (task.status === 'running' || task.status === 'queued')) cancelTask(task.item_id, task.base_dir);
      else if (direction === 'right' && task.status === 'queued') reorderTask(task.item_id, -1);
      else if (direction === 'right' && successStatus(task.status)) openDirectory(task.base_dir);
      else toast('任务进行中', '可使用顶部的停止全部', 'warning', 1800);
    });
    bindSwipeActions($('#shelfList'), '.shelf-item', (card, direction) => {
      if (direction === 'left') card.querySelector('[data-shelf-remove]')?.click();
      else card.querySelector('[data-shelf-open]')?.click();
    });

    let edge = null;
    const root = $('#appRoot');
    root?.addEventListener('pointerdown', event => {
      if (event.clientX > 26 || event.target.closest('input, textarea, select, .sheet, .reader-workspace')) return;
      edge = { x: event.clientX, y: event.clientY };
    });
    root?.addEventListener('pointerup', event => {
      if (!edge) return;
      const dx = event.clientX - edge.x, dy = event.clientY - edge.y;
      edge = null;
      if (dx > 78 && Math.abs(dx) > Math.abs(dy) * 1.3) { haptic(10); goBack(); }
    });
  }

  /* ───────────────────────── 事件绑定 ───────────────────────── */

  function bindEvents() {
    $$('.tab[data-view]').forEach(button => button.addEventListener('click', () => { haptic(6); showView(button.dataset.view); }));
    $$('[data-close-sheet]').forEach(button => button.addEventListener('click', () => closeTopSheet()));
    $('#scrim').addEventListener('click', () => closeTopSheet());
    $$('.sheet').forEach(bindSheetDrag);

    $('#themeToggle').addEventListener('click', toggleTheme);
    $('#themeSelect').addEventListener('change', event => applyTheme(event.target.value));
    $('#motionSelect')?.addEventListener('change', event => applyMotion(event.target.value));
    $('#hapticsInput')?.addEventListener('change', event => {
      store.setRaw('jm-haptics-v1', event.target.checked ? '1' : '0');
      if (event.target.checked) haptic(12);
    });
    $('#readerThemeSelect').addEventListener('change', event => {
      const value = event.target.value;
      store.set('jm-reader-theme-v1', value);
      window.JMReader?.setThemePreference?.(value);
    });

    /* 搜索 */
    $('#searchEntry').addEventListener('click', enterSearch);
    $('#searchCancel').addEventListener('click', () => exitSearch(true));
    $('#searchForm').addEventListener('submit', event => {
      event.preventDefault();
      $('#searchInput').blur();
      const value = $('#searchInput').value.trim();
      if (value) search(value); else exitSearch(true);
    });
    $('#searchInput').addEventListener('input', event => { $('#searchClear').hidden = !event.target.value; });
    $('#searchClear').addEventListener('click', () => {
      $('#searchInput').value = '';
      $('#searchClear').hidden = true;
      state.query = '';
      state.mode = 'ranking';
      $('#searchControls').hidden = true;
      syncSearchUi();
      $('#searchInput').focus();
    });
    $('#clearRecent').addEventListener('click', () => { state.recent = []; store.set('jm-recent-v2', []); renderRecent(); });
    $('#recentSearches').addEventListener('click', event => {
      const button = event.target.closest('[data-recent]');
      if (!button) return;
      $('#searchInput').value = button.dataset.recent;
      $('#searchClear').hidden = false;
      search(button.dataset.recent);
    });

    $('#quickShelf').addEventListener('click', () => { haptic(8); showView('shelf'); });
    $('#quickQueue').addEventListener('click', () => { haptic(8); showView('queue'); });
    $('#quickRefresh').addEventListener('click', async () => {
      $('#refreshButton').click();
    });
    $('#refreshButton').addEventListener('click', async () => {
      const button = $('#refreshButton');
      button.classList.add('is-spinning');
      haptic(8);
      try { if (state.mode === 'search' && state.query) await search(state.query); else await loadRanking(state.rank); }
      finally { button.classList.remove('is-spinning'); }
    });
    $('#rankingTabs').addEventListener('click', event => {
      const button = event.target.closest('[data-rank]');
      if (!button || button.classList.contains('is-active')) return;
      haptic(6);
      loadRanking(button.dataset.rank);
      currentScroll()?.scrollTo({ top: 0, behavior: 'smooth' });
    });
    $('#layoutPicker').addEventListener('click', event => {
      const button = event.target.closest('[data-layout]');
      if (!button || button.dataset.layout === state.layout) return;
      state.layout = button.dataset.layout;
      store.set('jm-layout-v1', state.layout);
      syncLayout();
    });
    $('#searchSortTabs').addEventListener('click', event => {
      const button = event.target.closest('[data-search-sort]');
      if (!button || button.dataset.searchSort === state.searchSort) return;
      state.searchSort = button.dataset.searchSort;
      store.set('jm-search-sort-v1', state.searchSort);
      syncSearchControls();
      if (state.query) search(state.query);
    });
    $('#searchTimeSelect').addEventListener('change', event => {
      state.searchTime = event.target.value;
      store.set('jm-search-time-v1', state.searchTime);
      syncSearchControls();
      if (state.query) search(state.query);
    });
    $('#resetSearchFilters').addEventListener('click', () => {
      state.searchSort = 'mr'; state.searchTime = 'a';
      store.set('jm-search-sort-v1', state.searchSort);
      store.set('jm-search-time-v1', state.searchTime);
      syncSearchControls();
      if (state.query) search(state.query);
    });
    $('#loadMoreButton').addEventListener('click', () => search(state.query, true));

    /* 作品卡片 */
    $('#albumGrid').addEventListener('click', event => {
      if (performance.now() - premiumState.longPressAt < 800) { event.preventDefault(); return; }
      const toggle = event.target.closest('[data-toggle-select]');
      if (toggle) { event.stopPropagation(); toggleSelected(toggle.dataset.toggleSelect); return; }
      const detail = event.target.closest('[data-detail]');
      if (detail) showDetail(detail.dataset.detail);
    });

    /* 下载清单 */
    $('#plannerToggleTop').addEventListener('click', () => openSheet($('#plannerSheet')));
    $('#floatingPlanButton').addEventListener('click', () => openSheet($('#plannerSheet')));
    $('#emptyBatchButton').addEventListener('click', openBatch);
    $('#selectedList').addEventListener('click', event => {
      const button = event.target.closest('[data-remove-selected]');
      if (button) toggleSelected(button.dataset.removeSelected);
    });
    $('#clearSelectedButton').addEventListener('click', () => { state.selected.clear(); renderSelection(); syncAlbumSelection(); });
    $('#confirmBatchButton').addEventListener('click', confirmBatch);
    $('#batchTextarea').addEventListener('input', updateBatchPreview);

    $('#formatPicker').addEventListener('click', event => {
      const button = event.target.closest('[data-format]');
      if (!button) return;
      $$('#formatPicker button').forEach(node => node.classList.toggle('is-active', node === button));
      $('#pdfModeRow').hidden = button.dataset.format !== 'pdf';
      queueConfigSave();
    });
    ['pathInput', 'imageFormatInput', 'pdfModeInput', 'photoThreadsInput', 'filenameLangInput', 'autoPathInput'].forEach(id => {
      $(`#${id}`)?.addEventListener('change', queueConfigSave);
    });
    $('#imageThreadsInput').addEventListener('input', event => { $('#imageThreadsValue').value = event.target.value; queueConfigSave(); });
    $('#albumThreadsInput').addEventListener('input', event => { $('#albumThreadsValue').value = event.target.value; queueConfigSave(); });
    $('#startDownloadButton').addEventListener('click', startDownload);
    $('#settingsOpenPath').addEventListener('click', () => openDirectory());
    $('#plannerOpenPath').addEventListener('click', () => openDirectory());

    /* 电脑端目录选择 */
    $('#folderBrowse').addEventListener('click', () => openFolderPicker('planner'));
    $('#folderUse').addEventListener('click', applyFolderPick);
    $('#folderUp').addEventListener('click', () => {
      const current = $('#folderPath').value.trim();
      const parent = current.replace(/[\\/][^\\/]*$/, '');
      loadFolder(parent === current ? '' : parent);
    });
    $('#folderList').addEventListener('click', event => {
      const pick = event.target.closest('[data-folder-pick]');
      if (pick) { $('#folderPath').value = pick.dataset.folderPick; renderFolderSuggestions(); return; }
      const button = event.target.closest('[data-folder-enter]');
      if (!button) return;
      const current = $('#folderPath').value.trim().replace(/[\\/]+$/, '');
      loadFolder(`${current}\\${button.dataset.folderEnter}`);
    });
    $('#folderPath').addEventListener('input', () => { if (android) renderFolderSuggestions(); });

    /* 任务 */
    $('#queueRefresh').addEventListener('click', async () => {
      const button = $('#queueRefresh');
      button.classList.add('is-spinning');
      try { await loadTasks(); } finally { button.classList.remove('is-spinning'); }
    });
    $('#stopAllButton').addEventListener('click', stopAll);
    $('#openRootButton').addEventListener('click', () => openDirectory());
    $('#clearLogsButton').addEventListener('click', () => { state.logs = []; store.set('jm-logs-v2', []); renderLogs(); });
    $('#queueSegments').addEventListener('click', event => {
      const button = event.target.closest('[data-queue-panel]');
      if (!button) return;
      $$('#queueSegments button').forEach(node => node.classList.toggle('is-active', node === button));
      $('#queuePanelTasks').hidden = button.dataset.queuePanel !== 'tasks';
      $('#queuePanelLogs').hidden = button.dataset.queuePanel !== 'logs';
    });
    $$('[data-stat-filter]').forEach(button => button.addEventListener('click', () => {
      state.statFilter = state.statFilter === button.dataset.statFilter && button.dataset.statFilter !== 'all' ? 'all' : button.dataset.statFilter;
      $$('[data-stat-filter]').forEach(node => node.classList.toggle('is-active', node.dataset.statFilter === state.statFilter));
      renderCache.taskLayout = null;
      renderTasks(state.snapshot.tasks || []);
    }));
    $('#taskList').addEventListener('click', event => {
      if (performance.now() - premiumState.longPressAt < 800) { event.preventDefault(); return; }
      const cancel = event.target.closest('[data-cancel-task]');
      if (cancel) { cancelTask(cancel.dataset.cancelTask, cancel.dataset.baseDir); return; }
      const reorder = event.target.closest('[data-reorder]');
      if (reorder) { reorderTask(reorder.dataset.id, reorder.dataset.reorder); return; }
      const open = event.target.closest('[data-open-task]');
      if (open) openDirectory(open.dataset.openTask);
    });

    /* 记录 */
    $('#historyFilters').addEventListener('click', event => {
      const button = event.target.closest('[data-history-filter]');
      if (!button) return;
      state.historyFilter = button.dataset.historyFilter;
      $$('#historyFilters .chip').forEach(node => node.classList.toggle('is-active', node === button));
      renderHistory();
    });
    $('#historyList').addEventListener('click', event => {
      const add = event.target.closest('[data-history-add]');
      if (add) {
        const id = add.dataset.historyAdd;
        if (!state.selected.has(id)) state.selected.set(id, { id, title: add.dataset.title || `作品 ${id}` });
        renderSelection(); syncAlbumSelection(id);
        openSheet($('#plannerSheet'));
        toast('已重新加入清单', `JM ${id}`);
        return;
      }
      const open = event.target.closest('[data-history-open]');
      if (open) openDirectory(open.dataset.historyOpen);
    });
    $('#clearHistoryButton').addEventListener('click', () => { state.history = []; store.set('jm-history-v2', []); renderCache.history = null; renderHistory(); toast('下载记录已清空'); });

    /* 连接设置 */
    $('#serverTestButton').addEventListener('click', async () => {
      const base = $('#serverUrlInput').value, token = $('#serverTokenInput').value;
      setConnectionTarget(base, token);
      toast('正在测试连接…', '', 'warning', 1400);
      await bootstrap();
    });

    matchMedia('(prefers-color-scheme: dark)').addEventListener?.('change', () => {
      if (store.get('jm-theme-v2', 'system') === 'system') applyTheme('system');
    });

    bindPremiumInteractions();
    attachPullToRefresh($('#discoverScroll'), async () => { if (state.mode === 'search' && state.query) await search(state.query); else await loadRanking(state.rank); });
    attachPullToRefresh($('#screen-shelf .screen-scroll'), async () => { await window.JMReader?.loadShelf(); });
    attachPullToRefresh($('#screen-queue .screen-scroll'), async () => { await loadTasks(); });
  }

  /* ───────────────────────── 启动 ───────────────────────── */

  async function bootstrap() {
    applyTheme();
    applyMotion();
    syncLayout();
    syncSearchControls();
    renderSelection();
    renderLogs();
    renderHistory();
    $('#serverUrlInput').value = conn.base;
    $('#serverTokenInput').value = conn.token;
    $('#readerThemeSelect').value = store.get('jm-reader-theme-v1', 'system');
    $('#hapticsInput').checked = store.raw('jm-haptics-v1', '1') !== '0';
    /* 应用内置服务时不需要连接配置，隐藏整组设置，避免误导。 */
    if (android) {
      $('#serverGroup').hidden = true;
      $('#folderUp').hidden = true;
      setText('#settingsPathHint', '下载目录在手机的公共存储里');
    }
    try {
      await api('/api/session');
      markConnected(true);
    } catch (error) {
      markConnected(false, error.message);
    }
    connectWebSocket();
    const readerReady = window.JMReader.init({
      api, apiRaw, icon, escapeHtml, toast, coverUrl, token: () => conn.token,
      demoMode, demoImage: demoImageResponse, nativeCall, android,
      closeSheets: closeAllSheets, closeTopSheet, openSheet, hasSheets: () => openSheets.length > 0,
      showView, currentView: () => state.view,
      applyTheme, toggleTheme, addDownload: book => {
        const id = String(book.id);
        if (!state.selected.has(id)) state.selected.set(id, { id, title: book.title });
        renderSelection();
        syncAlbumSelection(id);
      }
    });
    loadRanking('day');
    const shortcut = new URLSearchParams(location.search).get('view');
    if (shortcut && $(`.screen[data-screen="${shortcut}"]`)) showView(shortcut);
    await Promise.all([loadConfig(), loadTasks(), readerReady]);
    syncOfflineBanner();
    setInterval(() => {
      if (document.hidden) return;
      const socketOpen = state.ws && state.ws.readyState === WebSocket.OPEN;
      if (demoMode || !socketOpen) loadTasks();
    }, 4000);
  }

  document.addEventListener('visibilitychange', () => { if (!document.hidden && state.view === 'queue') loadTasks(); });

  window.JMApp = { showView, state, openSheet, closeTopSheet };
  bindEvents();
  bootstrap();

  /* 网页版登记 Service Worker 以支持离线打开；应用内不需要，
     否则升级 APK 后可能命中旧缓存。 */
  if (!android && 'serviceWorker' in navigator && location.protocol !== 'file:') {
    window.addEventListener('load', () => navigator.serviceWorker.register('sw.js').catch(() => { }));
  }
})();
