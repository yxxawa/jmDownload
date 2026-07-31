(() => {
  'use strict';

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const token = window.__JMDOWNLOAD_TOKEN__ || '';
  const desktopMode = !!window.__JMDOWNLOAD_DESKTOP__;
  const demoMode = new URLSearchParams(location.search).has('demo');
  const store = {
    get(key, fallback) {
      try { const value = localStorage.getItem(key); return value == null ? fallback : JSON.parse(value); }
      catch { return fallback; }
    },
    set(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch {} }
  };

  const defaultConfig = {
    base_dir: 'C:\\JMDownLoad', image_format: '.png', output_format: 'images', pdf_mode: 'merged',
    photo_threads: 1, image_threads: 5, album_threads: 1, filename_lang: 'traditional',
    auto_path: true, default_base_dir: 'C:\\JMDownLoad'
  };

  const demoAlbums = [
    ['438320', '夏日漫游指南', 1], ['422116', '午夜书店的来客', 2], ['397805', '和风小镇散步日记', 3],
    ['451928', '雨后的玻璃花房', 4], ['375214', '週末限定的秘密旅行', 5], ['469133', '琥珀色的午後时光', 6],
    ['408762', '城市边缘的观星者', 7], ['463501', '白昼梦与蓝色信箱', 8], ['349872', '沿海公路慢慢走', 9],
    ['470226', '风从庭院里经过', 10], ['389410', '咖啡冷掉以前', 11], ['456708', '昨日重现的唱片店', 12]
  ].map(([id, title, rank]) => ({ id, title, rank }));

  const searchSortLabels = { mr:'最新发布', mv:'浏览最多', mp:'页数最多', tf:'收藏最多' };
  const searchTimeLabels = { a:'全部时间', t:'今天', w:'本周', m:'本月' };
  const savedSearchSort = store.get('jm-search-sort-v1', 'mr');
  const savedSearchTime = store.get('jm-search-time-v1', 'a');

  const state = {
    view: 'discover', rank: 'day', mode: 'ranking', query: '', page: 1, hasNext: false,
    searchSort: searchSortLabels[savedSearchSort] ? savedSearchSort : 'mr',
    searchTime: searchTimeLabels[savedSearchTime] ? savedSearchTime : 'a',
    items: [], selected: new Map(), config: { ...defaultConfig }, snapshot: { running: false, stopping: false, tasks: [], last_success_ids: [], last_failed_ids: [] },
    logs: store.get('jm-logs-v2', []), history: store.get('jm-history-v2', []), recent: store.get('jm-recent-v2', []),
    historyFilter: 'all', saveTimer: 0, ws: null, reconnectTimer: 0, online: false, requestSerial: 0,
    tasksLoading: false, snapshotEventSerial: 0
  };

  const renderCache = {
    selectionItems: null,
    taskLayout: null,
    logs: null,
    history: null
  };
  const progressLogBuckets = new Map();

  const icon = name => `<svg aria-hidden="true"><use href="#i-${name}"/></svg>`;
  const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, ch => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', "'":'&#39;', '"':'&quot;' }[ch]));
  const clamp = (value, min, max) => Math.max(min, Math.min(max, Number(value) || min));
  const statusLabel = status => ({ queued:'等待中', running:'下载中', success:'已完成', completed:'已完成', failed:'异常', cancelled:'已取消', stopping:'正在停止' }[status] || status || '等待中');
  const successStatus = status => status === 'success' || status === 'completed' || status === 'done';
  const coverUrl = id => demoMode ? '' : `/api/cover/${encodeURIComponent(id)}${token ? `?token=${encodeURIComponent(token)}` : ''}`;
  const setText = (target, value) => {
    const node = typeof target === 'string' ? $(target) : target;
    const next = String(value ?? '');
    if (node && node.textContent !== next) node.textContent = next;
  };
  const formatTime = value => {
    const date = value ? new Date(value) : new Date();
    return date.toLocaleString('zh-CN', { month:'2-digit', day:'2-digit', hour:'2-digit', minute:'2-digit' });
  };

  async function api(path, options = {}) {
    if (demoMode) return demoApi(path, options);
    const headers = new Headers(options.headers || {});
    if (token) headers.set('Authorization', `Bearer ${token}`);
    if (options.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
    const response = await fetch(path, { ...options, headers });
    let payload = null;
    try { payload = await response.json(); } catch {}
    if (!response.ok) throw new Error(payload?.detail || `请求失败（${response.status}）`);
    return payload;
  }

  async function demoApi(path, options = {}) {
    await new Promise(resolve => setTimeout(resolve, path.includes('tasks') ? 80 : 420));
    if (path === '/api/config') {
      if ((options.method || 'GET') === 'PUT') {
        state.config = { ...state.config, ...JSON.parse(options.body || '{}') };
        return state.config;
      }
      return { ...defaultConfig, base_dir: 'D:\\Library\\JMDownLoad', default_base_dir: 'D:\\Library\\JMDownLoad' };
    }
    if (path.startsWith('/api/ranking')) return { items: demoAlbums };
    if (path.startsWith('/api/search')) {
      const params = new URL(path, 'http://demo').searchParams;
      const q = params.get('q') || '';
      const sort = params.get('sort') || 'mr';
      const time = params.get('time') || 'a';
      let items = demoAlbums.filter(x => x.title.includes(q) || x.id.includes(q)).map(x => ({ ...x, rank: null }));
      if (sort !== 'mr') items = [...items].reverse();
      return { items, page: 1, sort, time, has_next: false };
    }
    if (path.startsWith('/api/album/')) {
      const id = decodeURIComponent(path.split('/').pop());
      const item = demoAlbums.find(x => x.id === id) || { id, title: `作品 ${id}` };
      return { ...item, author: '示例作者', tags: ['剧情', '日常', '彩色'], page_count: 128 };
    }
    if (path === '/api/tasks' || path === '/health') return state.snapshot;
    if (path === '/api/download') {
      const body = JSON.parse(options.body || '{}');
      state.snapshot = {
        running: true, stopping: false, current_item_id: body.ids[0], last_success_ids: [], last_failed_ids: [],
        tasks: body.ids.map((id, index) => ({ item_id: id, status: index === 0 ? 'running' : 'queued', base_dir: body.base_dir, message: index === 0 ? '正在准备章节信息' : '等待前序任务', progress: index === 0 ? 28 : 0, total: 100, detail: '' }))
      };
      setTimeout(() => renderSnapshot(state.snapshot), 50);
      return { started: true, ids: body.ids };
    }
    if (path === '/api/download/stop') { state.snapshot.running = false; state.snapshot.stopping = true; return { stopping: true }; }
    if (path.startsWith('/api/download/cancel/')) return { cancelled: true };
    if (path === '/api/download/reorder') return { reordered: true };
    return {};
  }

  function setConnection(mode, text) {
    state.online = mode === 'online';
    const card = $('#connectionCard');
    card.classList.remove('is-online', 'is-offline');
    if (mode) card.classList.add(`is-${mode}`);
    $('#connectionText').textContent = text;
  }

  function applyTheme(preference = store.get('jm-theme-v2', 'system')) {
    const dark = preference === 'dark' || (preference === 'system' && matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    $('#themeLabel').textContent = dark ? '浅色模式' : '深色模式';
    $('#themeSelect').value = preference;
    store.set('jm-theme-v2', preference);
  }

  function toggleTheme() {
    const isDark = document.documentElement.dataset.theme === 'dark';
    applyTheme(isDark ? 'light' : 'dark');
  }

  function toast(title, message = '', type = 'success', duration = 3200) {
    const node = document.createElement('div');
    node.className = `toast ${type}`;
    node.innerHTML = `<span class="toast-icon">${icon(type === 'error' ? 'x' : type === 'warning' ? 'info' : 'check')}</span><div><strong>${escapeHtml(title)}</strong>${message ? `<p>${escapeHtml(message)}</p>` : ''}</div>`;
    $('#toastRegion').append(node);
    const remove = () => { node.classList.add('is-leaving'); setTimeout(() => node.remove(), 210); };
    setTimeout(remove, duration);
  }

  function showView(view) {
    state.view = view;
    $$('.nav-item[data-view]').forEach(button => {
      const active = button.dataset.view === view;
      button.classList.toggle('is-active', active);
      if (active) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    });
    $$('.view').forEach(node => node.classList.toggle('is-active', node.id === `view-${view}`));
    $('.main-content').scrollTo({ top: 0, behavior: 'smooth' });
    if (view === 'queue') loadTasks();
    if (view === 'history') renderHistory();
  }

  function openModal(modal) {
    closePlanner();
    $('#modalBackdrop').hidden = false;
    modal.hidden = false;
    setTimeout(() => (modal.querySelector('textarea, input, button') || modal).focus?.(), 30);
  }

  function closeModals() {
    $('#modalBackdrop').hidden = true;
    $$('.modal').forEach(modal => modal.hidden = true);
  }

  function openPlanner() {
    $('#planner').classList.add('is-open');
  }

  function closePlanner() {
    $('#planner').classList.remove('is-open');
    if ($$('.modal:not([hidden])').length === 0) $('#modalBackdrop').hidden = true;
  }

  function showSkeletons(count = 10) {
    $('#resultState').hidden = true;
    $('#albumGrid').innerHTML = Array.from({ length: count }, () => `<article class="skeleton-card"><div class="cover-wrap"></div><div class="skeleton-line"></div><div class="skeleton-line short"></div></article>`).join('');
  }

  function showResultState(title, message) {
    $('#albumGrid').innerHTML = '';
    const box = $('#resultState');
    box.innerHTML = `<strong>${escapeHtml(title)}</strong><p>${escapeHtml(message)}</p>`;
    box.hidden = false;
    $('#loadMoreWrap').hidden = true;
  }

  function syncSearchControls() {
    $$('#searchSortTabs button').forEach(button => button.classList.toggle('is-active', button.dataset.searchSort === state.searchSort));
    $('#searchTimeSelect').value = state.searchTime;
    $('#resetSearchFilters').hidden = state.searchSort === 'mr' && state.searchTime === 'a';
  }

  function searchSubtitle() {
    return `${searchSortLabels[state.searchSort]} · ${searchTimeLabels[state.searchTime]} · 点击封面查看详情`;
  }

  async function loadRanking(rank = state.rank) {
    state.mode = 'ranking'; state.rank = rank; state.page = 1; state.query = '';
    $('#searchControls').hidden = true;
    $('#contentTitle').textContent = '热门榜单';
    $('#contentSubtitle').textContent = rank === 'day' ? '看看今天大家都在下载什么' : rank === 'week' ? '一周内持续受到关注的作品' : '这个月反复被收藏的作品';
    $('#rankingTabs').hidden = false;
    $$('#rankingTabs button').forEach(button => button.classList.toggle('is-active', button.dataset.rank === rank));
    showSkeletons();
    const serial = ++state.requestSerial;
    try {
      const data = await api(`/api/ranking?type=${encodeURIComponent(rank)}`);
      if (serial !== state.requestSerial) return;
      state.items = data.items || [];
      state.hasNext = false;
      renderAlbums(state.items);
      setConnection('online', demoMode ? '演示模式' : '服务已连接');
    } catch (error) {
      setConnection('offline', '连接异常');
      showResultState('榜单加载失败', error.message);
      toast('榜单加载失败', error.message, 'error');
    }
  }

  async function search(query, append = false) {
    query = query.trim();
    if (!query) { loadRanking(state.rank); return; }
    state.mode = 'search'; state.query = query;
    state.page = append ? state.page + 1 : 1;
    $('#contentTitle').textContent = `“${query}” 的搜索结果`;
    $('#contentSubtitle').textContent = searchSubtitle();
    $('#rankingTabs').hidden = true;
    $('#searchControls').hidden = false;
    syncSearchControls();
    if (!append) showSkeletons(8);
    $('#loadMoreButton').disabled = true;
    rememberSearch(query);
    const serial = ++state.requestSerial;
    try {
      const params = new URLSearchParams({
        q: query,
        page: String(state.page),
        main_tag: '0',
        sort: state.searchSort,
        time: state.searchTime
      });
      const data = await api(`/api/search?${params}`);
      if (serial !== state.requestSerial) return;
      const incoming = data.items || [];
      state.items = append ? [...state.items, ...incoming] : incoming;
      state.hasNext = !!data.has_next && incoming.length > 0;
      renderAlbums(state.items);
    } catch (error) {
      if (append) state.page = Math.max(1, state.page - 1);
      else showResultState('没有拿到搜索结果', error.message);
      toast('搜索失败', error.message, 'error');
    } finally { $('#loadMoreButton').disabled = false; }
  }

  function renderAlbums(items) {
    $('#resultState').hidden = true;
    $('#loadMoreWrap').hidden = !(state.mode === 'search' && state.hasNext);
    if (!items.length) { showResultState('这里暂时是空的', '换一个关键词，或直接使用“批量 ID”加入下载清单。'); return; }
    $('#albumGrid').innerHTML = items.map((item, index) => {
      const selected = state.selected.has(String(item.id));
      const src = coverUrl(item.id);
      const eagerCover = index < 16;
      return `<article class="album-card" data-id="${escapeHtml(item.id)}" style="animation-delay:${Math.min(index, 12) * 28}ms">
        <div class="cover-wrap" data-detail="${escapeHtml(item.id)}">
          <span class="cover-placeholder">J</span>
          ${src ? `<img class="album-cover" src="${src}" alt="" loading="${eagerCover ? 'eager' : 'lazy'}" fetchpriority="${index < 8 ? 'high' : 'auto'}" decoding="async" onerror="this.style.display='none'">` : ''}
          ${item.rank ? `<span class="rank-badge ${item.rank <= 3 ? 'top' : ''}">#${item.rank}</span>` : ''}
          <button class="card-select ${selected ? 'is-selected' : ''}" data-toggle-select="${escapeHtml(item.id)}" data-selected="${selected ? '1' : '0'}" aria-label="${selected ? '移出' : '加入'}下载清单">${icon(selected ? 'check' : 'plus')}</button>
          <span class="cover-id">JM ${escapeHtml(item.id)}</span>
        </div>
        <div class="card-body">
          <h3 class="card-title" data-detail="${escapeHtml(item.id)}">${escapeHtml(item.title || `作品 ${item.id}`)}</h3>
          <div class="card-meta"><span>${item.rank ? `榜单第 ${item.rank} 名` : '搜索结果'}</span><button class="card-add" data-toggle-select="${escapeHtml(item.id)}">${selected ? '已加入' : '加入清单'}</button></div>
        </div>
      </article>`;
    }).join('');
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
      setText(addButton, selected ? '已加入' : '加入清单');
    });
  }

  function toggleSelected(id, itemOverride) {
    id = String(id);
    if (state.selected.has(id)) {
      state.selected.delete(id);
    } else {
      const item = itemOverride || state.items.find(entry => String(entry.id) === id) || { id, title: `作品 ${id}` };
      state.selected.set(id, { id, title: item.title || `作品 ${id}` });
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
    $('#selectionEmpty').hidden = count > 0;
    $('#selectedList').hidden = count === 0;
    $('#clearSelectedButton').style.visibility = count ? 'visible' : 'hidden';
    $('#startDownloadButton').disabled = count === 0 || state.snapshot.running;
    setText('#downloadHint', state.snapshot.running ? '当前有任务正在执行' : count ? `将下载 ${count} 个作品到所选目录` : '选择作品后即可开始');

    const itemKey = JSON.stringify(items.map(item => [String(item.id), item.title || `作品 ${item.id}`]));
    if (renderCache.selectionItems === itemKey) return;
    renderCache.selectionItems = itemKey;
    $('#selectedList').innerHTML = items.map(item => {
      const src = coverUrl(item.id);
      return `<div class="selected-item">
        ${src ? `<img class="selected-thumb" src="${src}" alt="" loading="lazy" decoding="async" onerror="this.style.display='none'">` : '<span class="selected-thumb"></span>'}
        <div class="selected-copy"><strong title="${escapeHtml(item.title)}">${escapeHtml(item.title)}</strong><small>JM ${escapeHtml(item.id)}</small></div>
        <button class="remove-selected" data-remove-selected="${escapeHtml(item.id)}" title="移出清单">${icon('x')}</button>
      </div>`;
    }).join('');
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

  function openBatchModal() {
    $('#batchTextarea').value = '';
    updateBatchPreview();
    openModal($('#batchModal'));
    setTimeout(() => $('#batchTextarea').focus(), 80);
  }

  function updateBatchPreview() {
    const ids = parseIds($('#batchTextarea').value);
    $('#batchPreview').innerHTML = `<span>${ids.length ? `已识别：${ids.slice(0, 4).join('、')}${ids.length > 4 ? '…' : ''}` : '等待输入'}</span><strong>${ids.length} 个有效 ID</strong>`;
    $('#confirmBatchButton').disabled = ids.length === 0;
  }

  function confirmBatch() {
    const ids = parseIds($('#batchTextarea').value);
    let added = 0;
    ids.forEach(id => {
      if (!state.selected.has(id)) { state.selected.set(id, { id, title: `作品 ${id}` }); added++; }
    });
    renderSelection();
    syncAlbumSelection();
    closeModals();
    openPlanner();
    toast('已加入下载清单', `新增 ${added} 个，共 ${state.selected.size} 个作品`);
  }

  async function showDetail(id) {
    const item = state.items.find(entry => String(entry.id) === String(id)) || state.selected.get(String(id));
    $('#detailContent').innerHTML = '<div class="detail-loading"><div class="spinner"></div><p>正在读取作品信息…</p></div>';
    openModal($('#detailModal'));
    try {
      const detail = await api(`/api/album/${encodeURIComponent(id)}`);
      const selected = state.selected.has(String(id));
      const src = coverUrl(id);
      $('#detailContent').innerHTML = `<div class="detail-hero">
        <div class="detail-cover-wrap"><span class="cover-placeholder">J</span>${src ? `<img class="detail-cover" src="${src}" alt="" onerror="this.style.display='none'">` : ''}</div>
        <div class="detail-info">
          <span class="detail-overline">JM ${escapeHtml(detail.id || id)}</span>
          <h2 id="detailTitle">${escapeHtml(detail.title || item?.title || `作品 ${id}`)}</h2>
          <p class="detail-author">${escapeHtml(detail.author ? `作者：${detail.author}` : '作者信息暂缺')}</p>
          <div class="detail-tags">${(detail.tags || []).slice(0, 8).map(tag => `<span>${escapeHtml(tag)}</span>`).join('') || '<span>暂无标签</span>'}</div>
          <div class="detail-stats"><div><strong>${detail.page_count || '—'}</strong><small>页数</small></div><div><strong>${(detail.tags || []).length}</strong><small>标签</small></div></div>
          <div class="detail-actions"><button class="primary-button" id="detailSelectButton">${icon(selected ? 'check' : 'plus')}${selected ? '已在清单中' : '加入下载清单'}</button><button class="secondary-button" data-close-modal>返回</button></div>
        </div>
      </div>`;
      $('#detailSelectButton').addEventListener('click', () => {
        toggleSelected(id, { id, title: detail.title || item?.title });
        const nowSelected = state.selected.has(String(id));
        $('#detailSelectButton').innerHTML = `${icon(nowSelected ? 'check' : 'plus')}${nowSelected ? '已在清单中' : '加入下载清单'}`;
      });
    } catch (error) {
      $('#detailContent').innerHTML = `<div class="detail-loading">${icon('info')}<strong>详情读取失败</strong><p>${escapeHtml(error.message)}</p></div>`;
    }
  }

  function rememberSearch(query) {
    state.recent = [query, ...state.recent.filter(item => item !== query)].slice(0, 5);
    store.set('jm-recent-v2', state.recent);
    renderRecent();
  }

  function renderRecent() {
    $('#recentRow').hidden = state.recent.length === 0;
    $('#recentSearches').innerHTML = state.recent.map(query => `<button data-recent="${escapeHtml(query)}">${escapeHtml(query)}</button>`).join('');
  }

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
  }

  async function loadConfig() {
    try {
      const config = await api('/api/config');
      applyConfigToForm(config);
      setSaveState('saved', '设置已同步');
    } catch (error) {
      applyConfigToForm(defaultConfig);
      setSaveState('error', '设置读取失败');
      toast('配置读取失败', error.message, 'error');
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
    state.saveTimer = setTimeout(() => saveConfig(false), 480);
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

  const bridgeRequests = new Map();
  window.addEventListener('jm-desktop-response', event => {
    const detail = event.detail || {};
    const pending = bridgeRequests.get(detail.id);
    if (!pending) return;
    bridgeRequests.delete(detail.id);
    detail.ok ? pending.resolve(detail.data) : pending.reject(new Error(detail.error || '桌面操作失败'));
  });

  function bridgeCall(type, payload = {}) {
    if (!desktopMode || !window.chrome?.webview) {
      if (demoMode) return Promise.resolve(type === 'selectDirectory' ? { cancelled: false, path: 'D:\\Library\\JMDownLoad' } : payload);
      return Promise.reject(new Error('桌面桥接尚未就绪'));
    }
    const id = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    return new Promise((resolve, reject) => {
      bridgeRequests.set(id, { resolve, reject });
      window.chrome.webview.postMessage({ id, type, payload });
      setTimeout(() => {
        if (!bridgeRequests.has(id)) return;
        bridgeRequests.delete(id);
        reject(new Error('桌面操作超时'));
      }, 30000);
    });
  }

  async function browseDirectory() {
    try {
      const result = await bridgeCall('selectDirectory', { path: $('#pathInput').value });
      if (!result?.cancelled && result?.path) {
        $('#pathInput').value = result.path;
        queueConfigSave();
        toast('保存位置已更新', result.path);
      }
    } catch (error) { toast('目录选择失败', error.message, 'error'); }
  }

  async function openDirectory(path = $('#pathInput').value) {
    try { await bridgeCall('openDirectory', { path }); }
    catch (error) { toast('打开目录失败', error.message, 'error'); }
  }

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
      closePlanner();
      showView('queue');
      await loadTasks();
      toast('任务已加入队列', `共 ${ids.length} 个作品，下载会在后台持续进行`);
    } catch (error) {
      toast('任务创建失败', error.message, 'error');
      button.disabled = false;
    } finally { button.querySelector('b').textContent = '开始下载'; }
  }

  async function loadTasks() {
    if (state.tasksLoading) return;
    state.tasksLoading = true;
    const eventSerial = state.snapshotEventSerial;
    try {
      const snapshot = await api('/api/tasks');
      if (eventSerial !== state.snapshotEventSerial) return;
      renderSnapshot(snapshot);
      setConnection('online', demoMode ? '演示模式' : '服务已连接');
    } catch (error) {
      setConnection('offline', '连接异常');
      if (state.view === 'queue') toast('任务状态刷新失败', error.message, 'error');
    } finally {
      state.tasksLoading = false;
    }
  }

  function renderSnapshot(snapshot) {
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
    const pillClass = `status-pill ${state.snapshot.stopping ? 'warning' : state.snapshot.running ? 'running' : 'neutral'}`;
    if (pill.className !== pillClass) pill.className = pillClass;
    setText(pill, state.snapshot.stopping ? '正在停止' : state.snapshot.running ? '运行中' : '空闲');
    $('#stopAllButton').disabled = !state.snapshot.running || state.snapshot.stopping;
    $('#queueBadge').hidden = !state.snapshot.running;
    renderSelection();
    renderTasks(tasks);
    syncHistoryFromSnapshot();
  }

  function taskPercent(task) {
    if (Number(task.total) > 0) return clamp(Math.round(Number(task.progress || 0) / Number(task.total) * 100), 0, 100);
    return successStatus(task.status) ? 100 : 0;
  }

  function taskLayoutKey(tasks) {
    if (!tasks.length) return '__empty__';
    return tasks.map((task, index) => [
      String(task.item_id),
      String(task.status || 'queued'),
      String(task.base_dir || ''),
      index,
      tasks.length
    ].join('\u0000')).join('\u0001');
  }

  function taskMarkup(task, index, totalTasks) {
    const status = task.status || 'queued';
    const percent = taskPercent(task);
    const statusIcon = successStatus(status) ? 'check' : status === 'failed' || status === 'cancelled' ? 'x' : status === 'running' ? 'download' : 'clock';
    const queued = status === 'queued';
    const active = queued || status === 'running';
    const message = task.message || task.detail || '等待处理';
    return `<article class="task-item is-${escapeHtml(status)}" data-task="${escapeHtml(task.item_id)}">
      <span class="task-status-icon">${icon(statusIcon)}</span>
      <div class="task-main"><div class="task-title-row"><strong>JM ${escapeHtml(task.item_id)}</strong><span>${statusLabel(status)} · ${percent}%</span></div><p title="${escapeHtml(task.detail || task.message)}">${escapeHtml(message)}</p><div class="progress-track"><i style="width:${percent}%"></i></div></div>
      <div class="task-actions">
        ${queued ? `<button data-reorder="-1" data-id="${escapeHtml(task.item_id)}" title="上移" ${index === 0 ? 'disabled' : ''}>${icon('arrow-up')}</button><button data-reorder="1" data-id="${escapeHtml(task.item_id)}" title="下移" ${index === totalTasks - 1 ? 'disabled' : ''}>${icon('arrow-down')}</button>` : ''}
        ${successStatus(status) ? `<button data-open-task="${escapeHtml(task.base_dir)}" title="打开目录">${icon('folder')}</button>` : ''}
        ${active ? `<button class="danger" data-cancel-task="${escapeHtml(task.item_id)}" data-base-dir="${escapeHtml(task.base_dir)}" title="取消任务">${icon('x')}</button>` : ''}
      </div>
    </article>`;
  }

  function renderTasks(tasks) {
    const layoutKey = taskLayoutKey(tasks);
    const list = $('#taskList');
    if (!tasks.length) {
      if (renderCache.taskLayout !== layoutKey) {
        list.innerHTML = `<div class="empty-state"><strong>队列很安静</strong><p>从“发现”页面选择作品，任务会按顺序出现在这里。</p></div>`;
        renderCache.taskLayout = layoutKey;
      }
      return;
    }

    if (renderCache.taskLayout !== layoutKey) {
      list.innerHTML = tasks.map((task, index) => taskMarkup(task, index, tasks.length)).join('');
      renderCache.taskLayout = layoutKey;
      return;
    }

    const nodes = new Map($$('[data-task]', list).map(node => [String(node.dataset.task), node]));
    tasks.forEach(task => {
      const node = nodes.get(String(task.item_id));
      if (!node) return;
      const status = task.status || 'queued';
      const percent = taskPercent(task);
      const message = task.message || task.detail || '等待处理';
      setText($('.task-title-row span', node), `${statusLabel(status)} · ${percent}%`);
      const detail = $('.task-main p', node);
      setText(detail, message);
      if (detail && detail.title !== String(task.detail || task.message || '')) detail.title = String(task.detail || task.message || '');
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

  function shouldRecordLog(event) {
    const itemId = event.item_id == null ? '' : String(event.item_id);
    if (event.type !== 'item_progress') {
      if (itemId && ['item_success', 'item_failed', 'item_cancelled'].includes(event.type)) progressLogBuckets.delete(itemId);
      return true;
    }

    const data = event.data || {};
    const progress = Number(data.progress || 0);
    const total = Number(data.total || 0);
    const stage = String(data.stage || 'progress');
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
    renderLogs();
  }

  function renderLogs() {
    const logKey = JSON.stringify(state.logs.map(entry => [entry.level, entry.message, entry.item_id, entry.time]));
    if (renderCache.logs === logKey) return;
    renderCache.logs = logKey;
    if (!state.logs.length) {
      $('#logList').innerHTML = `<div class="empty-state"><strong>暂无活动</strong><p>下载开始后，关键进度和异常会显示在这里。</p></div>`;
      return;
    }
    $('#logList').innerHTML = state.logs.map(entry => `<div class="log-entry ${String(entry.level).toLowerCase()}"><i class="log-dot"></i><div><p>${escapeHtml(entry.message)}</p><time>${formatTime(entry.time)}${entry.item_id ? ` · JM ${escapeHtml(entry.item_id)}` : ''}</time></div></div>`).join('');
  }

  function upsertHistory(entry) {
    const index = state.history.findIndex(item => String(item.id) === String(entry.id));
    if (index >= 0) {
      const current = state.history[index];
      const next = { ...current, ...entry };
      const unchanged = ['id', 'title', 'status', 'path', 'time'].every(key => String(current[key] ?? '') === String(next[key] ?? ''));
      if (unchanged) return false;
      state.history[index] = next;
    } else {
      state.history.unshift(entry);
    }
    state.history = state.history.slice(0, 150).sort((a, b) => new Date(b.time) - new Date(a.time));
    store.set('jm-history-v2', state.history);
    if (state.view === 'history') renderHistory();
    return true;
  }

  function syncHistoryFromSnapshot() {
    const successIds = new Set((state.snapshot.last_success_ids || []).map(String));
    const failedIds = new Set((state.snapshot.last_failed_ids || []).map(String));
    (state.snapshot.tasks || []).forEach(task => {
      const status = successIds.has(String(task.item_id)) || successStatus(task.status) ? 'success' : failedIds.has(String(task.item_id)) || task.status === 'failed' || task.status === 'cancelled' ? 'failed' : task.status;
      if (status === 'success' || status === 'failed') {
        const old = state.history.find(item => String(item.id) === String(task.item_id));
        upsertHistory({ id: String(task.item_id), title: old?.title || `作品 ${task.item_id}`, status, path: task.base_dir || old?.path || state.config.base_dir, time: old?.status === status ? old.time : new Date().toISOString() });
      }
    });
  }

  function renderHistory() {
    const items = state.history.filter(item => state.historyFilter === 'all' || item.status === state.historyFilter);
    const historyKey = JSON.stringify([state.historyFilter, items.map(item => [item.id, item.title, item.status, item.path, item.time])]);
    if (renderCache.history === historyKey) return;
    renderCache.history = historyKey;
    if (!items.length) {
      $('#historyList').innerHTML = `<div class="empty-state"><strong>${state.history.length ? '这个筛选下没有记录' : '还没有下载记录'}</strong><p>${state.history.length ? '切换到“全部”查看其他任务。' : '完成第一次下载后，这里会帮你记住作品和目录。'}</p></div>`;
      return;
    }
    $('#historyList').innerHTML = items.map(item => {
      const failed = item.status === 'failed';
      return `<article class="history-item ${failed ? 'failed' : ''}">
        <span class="history-icon">${icon(failed ? 'x' : item.status === 'running' ? 'download' : 'check')}</span>
        <div class="history-copy"><strong>${escapeHtml(item.title || `作品 ${item.id}`)}</strong><p>JM ${escapeHtml(item.id)} · ${escapeHtml(item.path || '保存目录未知')}</p></div>
        <div class="history-meta"><time>${formatTime(item.time)}</time><div><button data-history-add="${escapeHtml(item.id)}" data-title="${escapeHtml(item.title || '')}">再次下载</button><button data-history-open="${escapeHtml(item.path || '')}">打开目录</button></div></div>
      </article>`;
    }).join('');
  }

  function handleDownloadEvent(event) {
    addLog(event);
    const snapshot = event.data?.snapshot || event.snapshot;
    if (snapshot) {
      state.snapshotEventSerial++;
      renderSnapshot(snapshot);
    }
    else loadTasks();
    if (event.type === 'item_success') toast('下载完成', event.message || `JM ${event.item_id} 已保存`);
    if (event.type === 'item_failed') toast('任务出现异常', event.message || `JM ${event.item_id} 下载失败`, 'error', 5000);
    if (event.type === 'finished') toast('本轮任务已结束', event.message || '所有队列任务都已处理');
  }

  function connectWebSocket() {
    clearTimeout(state.reconnectTimer);
    if (demoMode) { setConnection('online', '演示模式'); return; }
    if (!token) { setConnection('offline', '等待桌面服务'); return; }
    try {
      const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
      const ws = new WebSocket(`${scheme}://${location.host}/ws/events?token=${encodeURIComponent(token)}`);
      state.ws = ws;
      ws.addEventListener('open', () => {
        setConnection('online', '服务已连接');
        loadTasks();
      });
      ws.addEventListener('message', message => {
        try { handleDownloadEvent(JSON.parse(message.data)); } catch {}
      });
      ws.addEventListener('close', () => {
        if (state.ws !== ws) return;
        setConnection('offline', '正在重新连接');
        state.reconnectTimer = setTimeout(connectWebSocket, 2500);
      });
      ws.addEventListener('error', () => ws.close());
    } catch {
      setConnection('offline', '正在重新连接');
      state.reconnectTimer = setTimeout(connectWebSocket, 2500);
    }
  }

  function bindEvents() {
    $$('.nav-item[data-view]').forEach(button => button.addEventListener('click', () => showView(button.dataset.view)));
    $('#themeToggle').addEventListener('click', toggleTheme);
    $('#themeSelect').addEventListener('change', event => applyTheme(event.target.value));
    $('#settingsButton').addEventListener('click', () => openModal($('#settingsModal')));
    $('#searchForm').addEventListener('submit', event => { event.preventDefault(); search($('#searchInput').value); });
    $('#refreshButton').addEventListener('click', async () => {
      const button = $('#refreshButton'); button.classList.add('is-spinning');
      if (state.mode === 'search') await search(state.query); else await loadRanking(state.rank);
      button.classList.remove('is-spinning');
    });
    $('#rankingTabs').addEventListener('click', event => { const button = event.target.closest('[data-rank]'); if (button) loadRanking(button.dataset.rank); });
    $('#searchSortTabs').addEventListener('click', event => {
      const button = event.target.closest('[data-search-sort]');
      if (!button || button.dataset.searchSort === state.searchSort) return;
      state.searchSort = button.dataset.searchSort;
      store.set('jm-search-sort-v1', state.searchSort);
      syncSearchControls();
      if (state.mode === 'search' && state.query) search(state.query);
    });
    $('#searchTimeSelect').addEventListener('change', event => {
      state.searchTime = event.target.value;
      store.set('jm-search-time-v1', state.searchTime);
      syncSearchControls();
      if (state.mode === 'search' && state.query) search(state.query);
    });
    $('#resetSearchFilters').addEventListener('click', () => {
      state.searchSort = 'mr'; state.searchTime = 'a';
      store.set('jm-search-sort-v1', state.searchSort);
      store.set('jm-search-time-v1', state.searchTime);
      syncSearchControls();
      if (state.mode === 'search' && state.query) search(state.query);
    });
    $('#loadMoreButton').addEventListener('click', () => search(state.query, true));
    $('#batchButton').addEventListener('click', openBatchModal);
    $('#emptyBatchButton').addEventListener('click', openBatchModal);
    $('#batchTextarea').addEventListener('input', updateBatchPreview);
    $('#confirmBatchButton').addEventListener('click', confirmBatch);
    $('#clearSelectedButton').addEventListener('click', () => { state.selected.clear(); renderSelection(); syncAlbumSelection(); });
    $('#plannerToggleTop').addEventListener('click', openPlanner);
    $('#floatingPlanButton').addEventListener('click', openPlanner);
    $('#plannerClose').addEventListener('click', closePlanner);
    $('#modalBackdrop').addEventListener('click', () => { closeModals(); closePlanner(); });
    document.addEventListener('click', event => { if (event.target.closest('[data-close-modal]')) closeModals(); });

    $('#albumGrid').addEventListener('click', event => {
      const toggle = event.target.closest('[data-toggle-select]');
      if (toggle) { event.stopPropagation(); toggleSelected(toggle.dataset.toggleSelect); return; }
      const detail = event.target.closest('[data-detail]');
      if (detail) showDetail(detail.dataset.detail);
    });
    $('#selectedList').addEventListener('click', event => { const button = event.target.closest('[data-remove-selected]'); if (button) toggleSelected(button.dataset.removeSelected); });
    $('#recentSearches').addEventListener('click', event => { const button = event.target.closest('[data-recent]'); if (button) { $('#searchInput').value = button.dataset.recent; search(button.dataset.recent); } });

    $('#formatPicker').addEventListener('click', event => {
      const button = event.target.closest('[data-format]'); if (!button) return;
      $$('#formatPicker button').forEach(node => node.classList.toggle('is-active', node === button));
      $('#pdfModeRow').hidden = button.dataset.format !== 'pdf';
      queueConfigSave();
    });
    ['pathInput', 'imageFormatInput', 'pdfModeInput', 'photoThreadsInput', 'filenameLangInput', 'autoPathInput'].forEach(id => $(`#${id}`).addEventListener('change', queueConfigSave));
    $('#imageThreadsInput').addEventListener('input', event => { $('#imageThreadsValue').value = event.target.value; queueConfigSave(); });
    $('#albumThreadsInput').addEventListener('input', event => { $('#albumThreadsValue').value = event.target.value; queueConfigSave(); });
    $('#browseButton').addEventListener('click', browseDirectory);
    $('#openPathButton').addEventListener('click', () => openDirectory());
    $('#openRootButton').addEventListener('click', () => openDirectory());
    $('#startDownloadButton').addEventListener('click', startDownload);
    $('#stopAllButton').addEventListener('click', stopAll);
    $('#clearLogsButton').addEventListener('click', () => { state.logs = []; store.set('jm-logs-v2', []); renderLogs(); });

    $('#taskList').addEventListener('click', event => {
      const cancel = event.target.closest('[data-cancel-task]'); if (cancel) { cancelTask(cancel.dataset.cancelTask, cancel.dataset.baseDir); return; }
      const reorder = event.target.closest('[data-reorder]'); if (reorder) { reorderTask(reorder.dataset.id, reorder.dataset.reorder); return; }
      const open = event.target.closest('[data-open-task]'); if (open) openDirectory(open.dataset.openTask);
    });
    $$('.filter-chip').forEach(button => button.addEventListener('click', () => {
      state.historyFilter = button.dataset.historyFilter;
      $$('.filter-chip').forEach(node => node.classList.toggle('is-active', node === button));
      renderHistory();
    }));
    $('#historyList').addEventListener('click', event => {
      const add = event.target.closest('[data-history-add]');
      if (add) { const id = add.dataset.historyAdd; if (!state.selected.has(id)) state.selected.set(id, { id, title: add.dataset.title || `作品 ${id}` }); renderSelection(); syncAlbumSelection(id); openPlanner(); toast('已重新加入清单', `JM ${id}`); return; }
      const open = event.target.closest('[data-history-open]'); if (open) openDirectory(open.dataset.historyOpen);
    });
    $('#clearHistoryButton').addEventListener('click', () => { state.history = []; store.set('jm-history-v2', []); renderHistory(); toast('下载记录已清空'); });

    document.addEventListener('keydown', event => {
      if (event.key === 'Escape') { closeModals(); closePlanner(); }
      if (event.ctrlKey && event.key.toLowerCase() === 'k') { event.preventDefault(); showView('discover'); $('#searchInput').focus(); $('#searchInput').select(); }
      if (event.ctrlKey && event.key === 'Enter') { event.preventDefault(); startDownload(); }
    });
    matchMedia('(prefers-color-scheme: dark)').addEventListener?.('change', () => { if (store.get('jm-theme-v2', 'system') === 'system') applyTheme('system'); });
  }

  async function init() {
    applyTheme();
    bindEvents();
    syncSearchControls();
    renderRecent();
    renderSelection();
    renderLogs();
    renderHistory();
    setConnection('', '正在连接');
    await Promise.all([loadConfig(), loadTasks(), loadRanking('day')]);
    connectWebSocket();
    setInterval(() => {
      if (document.hidden) return;
      const socketOpen = state.ws && state.ws.readyState === WebSocket.OPEN;
      if (demoMode || !socketOpen) loadTasks();
    }, 4500);
  }

  init();
})();
