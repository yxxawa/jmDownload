(() => {
  'use strict';

  const $ = selector => document.querySelector(selector);
  const $$ = selector => [...document.querySelectorAll(selector)];
  const defaults = { mode: 'vertical', fit: 'comfortable', direction: 'ltr', prefetch: 3, cache_mb: 512, theme: 'system' };

  const r = {
    adapter: null, active: false, book: null, session: '', chapter: null, page: 0, offset: 0, generation: 0,
    context: null, settings: { ...defaults }, bookmarks: [], zoom: 100, saveTimer: 0, windowTimer: 0,
    scrollFrame: 0, restoreView: 'discover', shelfFilter: 'recent', shelf: null, shelfSerial: 0,
    revision: 0, windowRevision: 0, settingsTimer: 0, settingsLoaded: false, immersive: false,
    toastTimer: 0, zoomBadgeTimer: 0, gesture: null, lastTap: 0
  };

  const esc = value => r.adapter.escapeHtml(value);
  const icon = name => r.adapter.icon(name);
  const api = (...args) => r.adapter.api(...args);
  const json = (method, value, extra = {}) => ({ method, body: JSON.stringify(value), ...extra });
  const clamp = (value, min, max) => Math.min(max, Math.max(min, Number(value) || 0));
  const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches || document.documentElement.dataset.motion === 'reduced';
  const nextRevision = () => r.revision = Math.max(Date.now() * 1000, r.revision + 1, Number(r.book?.progress?.revision || 0) + 1);
  const chapterIndex = () => r.book?.chapters?.findIndex(c => c.id === r.chapter?.id) ?? -1;
  const viewport = () => $('#readerViewport');
  const canvas = () => $('#readerCanvas');
  const isVertical = () => r.settings.mode === 'vertical';

  /* ───────────────────────── 生命周期 ───────────────────────── */

  function abortContext() {
    const context = r.context; r.context = null;
    if (!context) return;
    context.aborted = true;
    context.controller.abort();
    context.pages.forEach(p => { p.controller?.abort(); releaseUrl(p); });
    clearTimeout(r.windowTimer);
    cancelAnimationFrame(r.scrollFrame);
  }

  function releaseUrl(state) {
    if (state?.url && state.url.startsWith('blob:')) URL.revokeObjectURL(state.url);
    if (state) state.url = '';
  }

  function message(text, retry) {
    canvas().innerHTML = `<div class="reader-message">${esc(text)}${retry ? '<button id="readerChapterRetry">重新加载本章</button>' : ''}</div>`;
    if (retry) $('#readerChapterRetry').onclick = () => loadChapter(retry, r.page);
  }

  function readerToast(text, duration = 1800) {
    const node = $('#readerToast');
    node.textContent = text;
    node.hidden = false;
    node.style.animation = 'none';
    void node.offsetHeight;
    node.style.animation = '';
    clearTimeout(r.toastTimer);
    r.toastTimer = setTimeout(() => { node.hidden = true; }, duration);
  }

  async function init(adapter) {
    r.adapter = adapter;
    bind();
    try {
      r.settings = { ...defaults, ...await api('/api/reader/settings') };
      r.settingsLoaded = true;
      syncSettings();
      adapter.applyTheme(r.settings.theme);
    } catch { /* 下载功能不受阅读设置读取失败影响 */ }
  }

  async function open(albumId, options = {}) {
    if (r.active) await close(false);
    const generation = ++r.generation;
    r.restoreView = r.adapter.currentView();
    r.active = true; r.book = null; r.chapter = null; r.page = 0; r.offset = 0;
    r.session = ''; r.shelf = null;
    $('#readerWorkspace').hidden = false;
    document.body.classList.add('reader-open');
    /* 阅读时保持屏幕常亮，并把系统栏一起收起来做真正的全屏；退出时恢复。 */
    r.adapter.nativeCall?.('screen', { on: 1 });
    r.adapter.nativeCall?.('system-bars', { visible: 0 });
    r.adapter.closeSheets();
    setImmersive(false);
    closeDrawer();
    $('#readerOptions').hidden = true;
    $('#readerBookTitle').textContent = '正在打开作品…';
    $('#readerChapterTitle').textContent = '';
    $('#readerZoomBadge').hidden = true;
    message('正在读取作品与章节目录…');
    syncControls();
    try {
      const data = await api('/api/reader/sessions', json('POST', { album_id: String(albumId) }));
      if (!r.active || generation !== r.generation) {
        if (data.session_id) api(`/api/reader/sessions/${data.session_id}`, { method: 'DELETE' }).catch(() => { });
        return;
      }
      if (!data.book?.chapters?.length || !data.session_id) throw new Error('作品没有可读取的章节');
      r.session = data.session_id; r.book = data.book; r.bookmarks = data.bookmarks || [];
      r.settings = { ...defaults, ...data.settings };
      syncSettings(); renderDirectory();
      $('#readerBookTitle').textContent = r.book.title;
      const progress = !options.fromStart ? r.book.progress : null;
      const wanted = options.chapter || progress?.chapter_id || r.book.chapters[0].id;
      const chapter = r.book.chapters.some(c => c.id === wanted) ? wanted : r.book.chapters[0].id;
      await loadChapter(chapter, options.page ?? progress?.page ?? 0, options.page == null ? progress?.offset || 0 : 0);
    } catch (error) {
      if (!r.active || generation !== r.generation) return;
      message(`暂时无法打开作品：${error.message}`);
      r.adapter.toast('阅读打开失败', error.message, 'error');
    }
  }

  async function close(returnToWorkspace = true) {
    if (!r.active) return;
    const pending = saveProgress(true), session = r.session;
    r.active = false; ++r.generation;
    abortContext(); clearTimeout(r.saveTimer);
    $('#readerWorkspace').hidden = true;
    $('#readerOptions').hidden = true;
    closeDrawer();
    document.body.classList.remove('reader-open');
    r.adapter.nativeCall?.('screen', { on: 0 });
    r.adapter.nativeCall?.('system-bars', { visible: 1 });
    r.session = '';
    await pending;
    if (session) await api(`/api/reader/sessions/${session}`, { method: 'DELETE' }).catch(() => { });
    if (returnToWorkspace) r.adapter.showView(r.restoreView);
  }

  /* ───────────────────────── 章节与页面 ───────────────────────── */

  async function loadChapter(chapterId, page = 0, offset = 0) {
    if (!r.active || !r.session) return;
    if (r.chapter) await saveProgress(true);
    abortContext();
    const generation = ++r.generation;
    const context = { generation, controller: new AbortController(), pages: new Map(), slots: [], allowed: new Set(), pending: 0, aborted: false, restoring: true };
    r.context = context; r.chapter = null;
    $('#readerChapterTitle').textContent = r.book.chapters.find(c => c.id === chapterId)?.title || '';
    message('正在加载当前章节…');
    syncControls();
    try {
      const chapter = await api(`/api/reader/chapters/${encodeURIComponent(chapterId)}?session=${encodeURIComponent(r.session)}`, { signal: context.controller.signal });
      if (!r.active || r.context !== context) return;
      if (!chapter.page_count || !chapter.pages?.length) throw new Error('章节没有图片');
      r.chapter = chapter;
      r.page = clamp(page, 0, chapter.page_count - 1);
      r.offset = clamp(offset, 0, 1);
      $('#readerChapterTitle').textContent = `${chapter.title}${chapter.local ? ' · 本地' : ''}`;
      renderPages(context);
      renderDirectory();
      syncControls();
      context.pendingRestore = { page: r.page, offset: r.offset };
      positionPage(r.page, r.offset);
      await updateWindow(context, true);
      requestAnimationFrame(() => {
        if (r.context !== context) return;
        jump(r.page, r.offset, false);
        context.restoring = false;
        updateVisible();
      });
    } catch (error) {
      if (!r.active || r.context !== context || error.name === 'AbortError') return;
      message(`章节读取失败：${error.message}`, chapterId);
    }
  }

  function renderPages(context = r.context) {
    if (!context || !r.chapter) return;
    const node = canvas();
    node.dataset.mode = r.settings.mode;
    node.dataset.fit = r.settings.fit;
    node.dataset.direction = r.settings.direction;
    node.style.setProperty('--reader-zoom', String(r.zoom / 100));
    $('#readerWorkspace').dataset.mode = r.settings.mode;
    const indexes = isVertical() ? Array.from({ length: r.chapter.page_count }, (_, i) => i)
      : r.settings.mode === 'spread' ? [r.page, r.page + 1].filter(i => i < r.chapter.page_count) : [r.page];
    node.innerHTML = indexes.map(i => `<div class="reader-page" data-page="${i}" data-status="idle"><div class="reader-placeholder">第 ${i + 1} 页 · 等待加载</div></div>`).join('');
    context.slots = [...node.querySelectorAll('.reader-page')];
    context.slots.forEach(slot => paint(slot, context.pages.get(Number(slot.dataset.page))));
  }

  function paint(slot, pageState) {
    const index = Number(slot.dataset.page);
    slot.dataset.status = pageState?.status || 'idle';
    if (pageState?.width && pageState.height) {
      slot.style.aspectRatio = `${pageState.width} / ${pageState.height}`;
      slot.style.setProperty('--natural-width', `${pageState.width}px`);
    }
    if (pageState?.status === 'ready') {
      const existing = slot.querySelector('img');
      if (existing?.src === pageState.url) return;
      slot.innerHTML = `<img alt="第 ${index + 1} 页" decoding="async">`;
      slot.querySelector('img').src = pageState.url;
    } else if (pageState?.status === 'error') {
      slot.innerHTML = `<div class="reader-placeholder"><span>第 ${index + 1} 页加载失败</span><small>${esc(pageState.error)}</small><button class="reader-retry" data-retry-page="${index}">重试这一页</button></div>`;
    } else {
      const label = pageState?.status === 'loading' ? '正在加载…' : '等待加载';
      if (slot.querySelector('img') || slot.dataset.label !== label) slot.innerHTML = `<div class="reader-placeholder">第 ${index + 1} 页 · ${label}</div>`;
      slot.dataset.label = label;
    }
  }

  function visibleRange() {
    if (!r.context || !r.chapter) return [r.page, r.page];
    if (!isVertical()) return [r.page, Math.min(r.chapter.page_count - 1, r.page + (r.settings.mode === 'spread' ? 1 : 0))];
    const box = viewport().getBoundingClientRect();
    const visible = r.context.slots.filter(slot => {
      const rect = slot.getBoundingClientRect();
      return rect.bottom > box.top + 2 && rect.top < box.bottom - 2;
    });
    if (!visible.length) return [r.page, r.page];
    return [Number(visible[0].dataset.page), Number(visible[visible.length - 1].dataset.page)];
  }

  async function updateWindow(context = r.context, immediate = false) {
    if (!context || r.context !== context || !r.chapter) return;
    const [start, end] = visibleRange();
    const first = Math.max(0, start - 1);
    const last = Math.min(r.chapter.page_count - 1, end + Number(r.settings.prefetch));
    context.allowed = new Set(Array.from({ length: last - first + 1 }, (_, i) => i + first));
    /* 只保留少量页面留在内存里，空槽位继续占位以保持滚动高度稳定。 */
    context.pages.forEach((state, i) => {
      if (context.allowed.has(i)) return;
      state.controller?.abort();
      releaseUrl(state);
      if (state.status !== 'error') state.status = 'idle';
      const slot = context.slots.find(n => Number(n.dataset.page) === i);
      if (slot) paint(slot, state);
    });
    clearTimeout(r.windowTimer);
    const chapter = r.chapter.id, session = r.session, revision = ++r.windowRevision;
    const announce = () => api(`/api/reader/sessions/${session}/window`, json('PATCH', { chapter_id: chapter, start, end, revision })).catch(() => { });
    if (immediate) await announce();
    else r.windowTimer = setTimeout(announce, 120);
    pump(context);
  }

  function pump(context) {
    if (context.aborted || r.context !== context || !r.chapter) return;
    const current = visibleRange();
    const visible = new Set(Array.from({ length: current[1] - current[0] + 1 }, (_, n) => current[0] + n));
    const wanted = [...context.allowed].sort((a, b) =>
      (visible.has(b) ? 1 : 0) - (visible.has(a) ? 1 : 0) ||
      Math.abs(a - r.page) - Math.abs(b - r.page));
    for (const i of wanted) {
      if (context.pending >= 5) break;
      const old = context.pages.get(i);
      if (old && old.status !== 'idle') continue;
      loadPage(context, i);
    }
  }

  async function loadPage(context, index) {
    const state = context.pages.get(index) || { status: 'idle' };
    state.status = 'loading';
    state.controller = new AbortController();
    context.pages.set(index, state);
    context.pending++;
    const controller = state.controller;
    const source = r.chapter.pages[index].url;
    const slot = () => context.slots.find(n => Number(n.dataset.page) === index);
    if (slot()) paint(slot(), state);
    try {
      let width = 0, height = 0, url = '';
      if (r.adapter.demoMode) {
        url = r.adapter.demoImage(source);
        const image = new Image();
        image.src = url;
        await image.decode();
        width = image.naturalWidth; height = image.naturalHeight;
      } else {
        const target = new URL(source, location.href);
        if (r.adapter.token()) target.searchParams.set('token', r.adapter.token());
        const response = await fetch(target.pathname + target.search, { signal: controller.signal });
        if (!response.ok) { let payload; try { payload = await response.json(); } catch { } throw new Error(payload?.detail || `图片请求失败 (${response.status})`); }
        const blob = await response.blob();
        if (context.aborted || r.context !== context || controller.signal.aborted) return;
        url = URL.createObjectURL(blob);
        const image = new Image();
        image.src = url;
        try { await image.decode(); } catch { URL.revokeObjectURL(url); throw new Error('图片格式无效或未正确还原'); }
        width = image.naturalWidth; height = image.naturalHeight;
      }
      if (context.aborted || r.context !== context || controller.signal.aborted) {
        if (url.startsWith('blob:')) URL.revokeObjectURL(url);
        return;
      }
      state.url = url; state.width = width; state.height = height; state.status = 'ready';
      /* 前置占位页拿到真实比例后，保持当前阅读锚点不跳动。 */
      const box = viewport();
      const anchor = context.slots.find(n => Number(n.dataset.page) === r.page);
      const before = anchor?.getBoundingClientRect().top;
      if (slot()) paint(slot(), state);
      if (isVertical() && anchor && before != null && index < r.page) {
        box.scrollTop += anchor.getBoundingClientRect().top - before;
      }
      if (context.pendingRestore?.page === index && r.page === index) {
        const target = context.pendingRestore;
        context.pendingRestore = null;
        jump(target.page, target.offset, false);
      }
    } catch (error) {
      if (context.aborted || r.context !== context) return;
      if (error.name === 'AbortError') state.status = 'idle';
      else { state.status = 'error'; state.error = error.message; if (slot()) paint(slot(), state); }
    } finally {
      if (state.controller === controller) state.controller = null;
      context.pending--;
      if (r.context === context && !context.aborted) pump(context);
    }
  }

  function updateVisible() {
    if (!r.active || !r.chapter || !r.context || r.context.restoring) return;
    if (isVertical()) {
      const [start] = visibleRange();
      r.page = start;
      const slot = r.context.slots.find(n => Number(n.dataset.page) === start);
      if (slot) {
        const box = viewport().getBoundingClientRect();
        const rect = slot.getBoundingClientRect();
        r.offset = clamp((box.top - rect.top) / rect.height, 0, 1);
      }
    }
    syncControls();
    updateWindow();
    scheduleSave();
  }

  function positionPage(page, offset = 0) {
    if (!isVertical()) return;
    const box = viewport();
    const slot = r.context?.slots.find(n => Number(n.dataset.page) === page);
    if (!slot) return;
    box.scrollTop += slot.getBoundingClientRect().top - box.getBoundingClientRect().top + slot.getBoundingClientRect().height * offset;
  }

  function jump(page, offset = 0, save = true) {
    if (!r.chapter || !r.context) return;
    r.page = clamp(page, 0, r.chapter.page_count - 1);
    r.offset = offset;
    if (save) r.context.pendingRestore = null;
    if (isVertical()) positionPage(r.page, offset);
    else { renderPages(); viewport().scrollTop = 0; }
    syncControls();
    updateWindow();
    if (save) scheduleSave();
  }

  function stepPage(direction) {
    if (!r.chapter) return;
    const step = r.settings.mode === 'spread' ? 2 : 1;
    const page = r.page + direction * step;
    if (page < 0) stepChapter(-1, true);
    else if (page >= r.chapter.page_count) stepChapter(1);
    else jump(page);
  }

  function stepChapter(direction, last = false) {
    const i = chapterIndex();
    const next = r.book?.chapters?.[i + direction];
    if (next) { loadChapter(next.id, last ? 100000 : 0); readerToast(`${direction < 0 ? '上一章' : '下一章'} · ${next.title}`, 1400); }
    else readerToast(direction < 0 ? '已经是第一章' : '已经是最后一章', 1400);
  }

  /* ───────────────────────── 控件同步 ───────────────────────── */

  function syncControls() {
    const count = r.chapter?.page_count || 0;
    const i = chapterIndex();
    $('#readerPageChip').textContent = `${count ? r.page + 1 : 0} / ${count || '—'}`;
    const progress = $('#readerProgress');
    progress.max = Math.max(0, count - 1);
    progress.value = r.page;
    progress.disabled = !count;
    $('#readerPrevChapter').disabled = i <= 0;
    $('#readerNextChapter').disabled = i < 0 || i >= ((r.book?.chapters?.length || 0) - 1);
    $('#readerPrevPage').disabled = !count || (r.page === 0 && i <= 0);
    $('#readerNextPage').disabled = !count || (r.page >= count - 1 && i >= (r.book?.chapters?.length || 1) - 1);
    const mini = $('#readerMiniProgress');
    if (mini) {
      const percent = count > 1 ? (r.page / (count - 1)) * 100 : 100;
      mini.style.setProperty('--reader-progress', `${Math.max(0, Math.min(100, percent))}%`);
      mini.setAttribute('aria-valuenow', String(Math.round(percent)));
    }
    const marked = r.bookmarks.some(b => b.chapter_id === r.chapter?.id && b.page === r.page);
    const bookmarkButton = $('#readerBookmarkButton');
    if (bookmarkButton) {
      bookmarkButton.classList.toggle('is-active', marked);
      bookmarkButton.setAttribute('aria-pressed', String(marked));
      bookmarkButton.disabled = !count;
    }
    $('#readerDownload').disabled = !r.book;
  }

  function syncSettings() {
    [['readerMode', 'mode'], ['readerFit', 'fit'], ['readerDirection', 'direction'], ['readerPrefetch', 'prefetch'], ['readerCacheBudget', 'cache_mb']].forEach(([id, key]) => {
      const select = $(`#${id}`);
      if (!select) return;
      const value = String(r.settings[key]);
      if (![...select.options].some(o => o.value === value)) select.add(new Option(value, value));
      select.value = value;
    });
    const zoom = $('#readerZoom');
    if (zoom) { zoom.value = String(r.zoom); $('#readerZoomValue').textContent = `${r.zoom}%`; }
    canvas().style.setProperty('--reader-zoom', String(r.zoom / 100));
  }

  function scheduleSave() { clearTimeout(r.saveTimer); r.saveTimer = setTimeout(() => saveProgress(), 650); }

  async function saveProgress(immediate = false) {
    clearTimeout(r.saveTimer);
    if (!r.book || !r.chapter) return;
    const id = r.book.id;
    const progress = { chapter_id: r.chapter.id, page: r.page, offset: r.offset, revision: nextRevision() };
    r.book.progress = progress;
    try {
      await api(`/api/reader/progress/${encodeURIComponent(id)}`, json('PUT', progress, { keepalive: immediate }));
      if (r.book?.id === id) $('#readerSaveStatus').textContent = '进度已保存';
    } catch {
      if (r.book?.id === id) $('#readerSaveStatus').textContent = '进度未同步';
    }
  }

  function saveSettings() {
    clearTimeout(r.settingsTimer);
    r.settingsTimer = setTimeout(() => api('/api/reader/settings', json('PUT', r.settings)).catch(e => r.adapter.toast('阅读设置保存失败', e.message, 'error')), 260);
  }

  /* ───────────────────────── 目录与书签 ───────────────────────── */

  function renderDirectory() {
    if (!r.book) return;
    $('#readerChapters').innerHTML = r.book.chapters.map(c => `<button data-reader-chapter="${esc(c.id)}" class="${c.id === r.chapter?.id ? 'is-current' : ''}">${esc(c.sort || '')} · ${esc(c.title)}</button>`).join('');
    $('#readerBookmarks').innerHTML = r.bookmarks.length
      ? r.bookmarks.map(b => `<button data-reader-chapter="${esc(b.chapter_id)}" data-reader-page="${b.page}">${esc(r.book.chapters.find(c => c.id === b.chapter_id)?.title || '章节')} · 第 ${b.page + 1} 页</button>`).join('')
      : '<div class="muted">还没有书签</div>';
  }

  function openDrawer() {
    $('#readerDirectory').hidden = false;
    $('#readerDrawerScrim').hidden = false;
    syncControls();
    const current = $('#readerChapters .is-current');
    current?.scrollIntoView({ block: 'center' });
  }

  function closeDrawer() {
    $('#readerDirectory').hidden = true;
    $('#readerDrawerScrim').hidden = true;
  }

  async function bookmark() {
    if (!r.chapter || !r.book) return;
    const value = { album_id: r.book.id, chapter_id: r.chapter.id, page: r.page };
    try {
      const result = await api('/api/reader/bookmarks', json('POST', value));
      r.bookmarks = r.bookmarks.filter(b => !(b.chapter_id === value.chapter_id && b.page === value.page));
      if (result.bookmarked) r.bookmarks.push(value);
      syncControls();
      renderDirectory();
      r.adapter.toast(result.bookmarked ? '已添加书签' : '已移除书签', `第 ${value.page + 1} 页`);
    } catch (error) {
      r.adapter.toast('书签保存失败', error.message, 'error');
    }
  }

  /* ───────────────────────── 沉浸模式 / 缩放 ───────────────────────── */

  function setImmersive(enabled) {
    r.immersive = enabled;
    $('#readerWorkspace').classList.toggle('is-immersive', enabled);
  }

  function showZoomBadge(text) {
    const badge = $('#readerZoomBadge');
    badge.textContent = text;
    badge.hidden = false;
    clearTimeout(r.zoomBadgeTimer);
    r.zoomBadgeTimer = setTimeout(() => { badge.hidden = true; }, 900);
  }

  function setZoom(value, announce = true) {
    r.zoom = clamp(Math.round(value), 50, 200);
    canvas().style.setProperty('--reader-zoom', String(r.zoom / 100));
    const slider = $('#readerZoom');
    if (slider) slider.value = String(r.zoom);
    const output = $('#readerZoomValue');
    if (output) output.textContent = `${r.zoom}%`;
    if (announce) showZoomBadge(`${r.zoom}%`);
  }

  /* ───────────────────────── 书架 ───────────────────────── */

  async function loadShelf() {
    const serial = ++r.shelfSerial;
    if (!r.shelf) $('#shelfList').innerHTML = '<div class="empty-state"><p>正在读取书架…</p></div>';
    try {
      const data = await api('/api/reader/shelf');
      if (serial !== r.shelfSerial) return;
      r.shelf = { books: data.books || [], bookmarks: data.bookmarks || [] };
      renderShelf();
    } catch (error) {
      if (serial !== r.shelfSerial) return;
      if (!r.shelf) $('#shelfList').innerHTML = `<div class="empty-state"><strong>书架读取失败</strong><p>${esc(error.message)}</p></div>`;
      else r.adapter.toast('书架更新失败', error.message, 'error');
    }
  }

  function renderShelf() {
    if (!r.shelf) return;
    let rows = r.shelf.books;
    if (r.shelfFilter === 'favorite') rows = rows.filter(b => b.favorite);
    if (r.shelfFilter === 'local') rows = rows.filter(b => b.local);
    if (r.shelfFilter === 'bookmarks') rows = r.shelf.bookmarks.map(mark => ({ ...r.shelf.books.find(b => b.id === mark.album_id), mark })).filter(b => b.id);
    $('#shelfCount').textContent = `${rows.length} 项`;
    $('#shelfList').innerHTML = rows.length ? rows.map(book => {
      const progress = book.mark || book.progress;
      const chapter = book.chapters?.find(c => c.id === progress?.chapter_id);
      const description = progress ? `${chapter?.title || '章节'} · 第 ${Number(progress.page) + 1} 页` : '尚未阅读';
      const cover = r.adapter.coverUrl(book.id);
      return `<article class="shelf-item" data-shelf-item="${esc(book.id)}">
        ${cover ? `<img class="shelf-cover" src="${cover}" alt="" loading="lazy" decoding="async">` : '<span class="shelf-cover"></span>'}
        <div class="shelf-copy"><strong>${esc(book.title)}</strong><p>${esc(description)}${book.local ? ' · 本地可读' : ''}</p></div>
        <div class="shelf-actions">
          <button class="shelf-read" data-shelf-open="${esc(book.id)}" ${book.mark ? `data-chapter="${esc(book.mark.chapter_id)}" data-page="${book.mark.page}"` : ''}>${progress ? '续读' : '阅读'}</button>
          <button data-shelf-favorite="${esc(book.id)}" data-favorite="${book.favorite ? '0' : '1'}" class="${book.favorite ? 'is-favorite' : ''}" aria-label="${book.favorite ? '取消收藏' : '收藏'}"><svg aria-hidden="true"><use href="#i-star"/></svg></button>
          <button data-shelf-remove="${esc(book.id)}" aria-label="移出书架">${icon('x')}</button>
        </div>
      </article>`;
    }).join('') : `<div class="empty-state"><strong>${r.shelfFilter === 'local' ? '还没有已完成的本地作品' : '这里还没有内容'}</strong><p>${r.shelfFilter === 'local' ? '完整的图片下载会自动登记到这里' : '从作品详情开始阅读，进度和书签会保存下来'}</p></div>`;
  }

  /* ───────────────────────── 缓存 ───────────────────────── */

  async function cacheStats() {
    try {
      const stats = await api('/api/reader/cache');
      $('#readerCacheStats').textContent = `${(Number(stats.bytes || 0) / 1048576).toFixed(1)} MB / ${Math.round(Number(stats.budget_bytes || 0) / 1048576)} MB`;
    } catch {
      $('#readerCacheStats').textContent = '缓存信息暂不可用';
    }
  }

  /* ───────────────────────── 手势 ───────────────────────── */

  function bindGestures() {
    const box = viewport();
    let pinchStart = 0, pinchZoom = 100, swiping = false, startX = 0, startY = 0, startTime = 0, dx = 0;
    let longPressTimer = 0, longPressTriggered = false, bookmarkCandidate = false;

    const cancelLongPress = () => {
      if (longPressTimer) clearTimeout(longPressTimer);
      longPressTimer = 0;
      box.classList.remove('is-pressing');
    };

    const resetSwipe = () => {
      const node = canvas();
      node.classList.remove('is-swiping', 'is-snapping');
      node.style.transform = '';
      swiping = false; dx = 0;
    };

    box.addEventListener('touchstart', event => {
      if (event.touches.length === 2) {
        cancelLongPress();
        bookmarkCandidate = false;
        const [a, b] = event.touches;
        pinchStart = Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
        pinchZoom = r.zoom;
        resetSwipe();
        return;
      }
      if (event.touches.length !== 1) return;
      const touch = event.touches[0];
      const image = event.target?.closest?.('.reader-page img');
      const rect = image?.getBoundingClientRect?.();
      const zoneWidth = rect ? Math.min(112, rect.width * .30) : 0;
      const zoneHeight = rect ? Math.min(144, rect.height * .22) : 0;
      bookmarkCandidate = Boolean(image && rect &&
        touch.clientX >= rect.left && touch.clientX <= rect.left + zoneWidth &&
        touch.clientY >= rect.top && touch.clientY <= rect.top + zoneHeight);
      startX = touch.clientX; startY = touch.clientY; startTime = performance.now(); dx = 0;
      swiping = false; longPressTriggered = false; cancelLongPress();
      if (bookmarkCandidate) {
        longPressTimer = window.setTimeout(() => {
          longPressTriggered = true;
          bookmarkCandidate = false;
          box.classList.add('is-pressing');
          document.body.dataset.jmLongPress = '1';
          window.setTimeout(() => { delete document.body.dataset.jmLongPress; }, 850);
          try { navigator.vibrate?.(18); } catch { }
          r.adapter.nativeCall?.('haptic', { kind: 'strong' });
          bookmark();
        }, 560);
      }
    }, { passive: true });

    box.addEventListener('touchmove', event => {
      if (event.touches.length === 2 && pinchStart > 0) {
        const [a, b] = event.touches;
        const distance = Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
        if (distance > 8) setZoom(pinchZoom * (distance / pinchStart), false);
        showZoomBadge(`${r.zoom}%`);
        return;
      }
      if (event.touches.length !== 1) return;
      const touch = event.touches[0];
      const moveX = touch.clientX - startX;
      const moveY = touch.clientY - startY;
      if (Math.abs(moveX) > 10 || Math.abs(moveY) > 10) {
        cancelLongPress();
        bookmarkCandidate = false;
      }
      if (isVertical()) return;
      if (!swiping) {
        if (Math.abs(moveX) < 12 || Math.abs(moveX) < Math.abs(moveY)) return;
        swiping = true;
        canvas().classList.add('is-swiping');
      }
      dx = moveX * (r.settings.direction === 'rtl' ? -1 : 1);
      canvas().style.transform = `translate3d(${dx}px, 0, 0)`;
    }, { passive: true });

    const finish = event => {
      const touch = event.changedTouches?.[0];
      if (pinchStart > 0 && event.touches.length === 0) {
        pinchStart = 0;
        saveSettings();
        setTimeout(() => { $('#readerZoomBadge').hidden = true; }, 600);
        return;
      }
      if (!touch) { cancelLongPress(); bookmarkCandidate = false; resetSwipe(); return; }
      cancelLongPress();
      bookmarkCandidate = false;
      if (longPressTriggered) { longPressTriggered = false; resetSwipe(); return; }
      const moveX = touch.clientX - startX;
      const moveY = touch.clientY - startY;
      const elapsed = performance.now() - startTime;
      const edgeSwipe = startX <= 34 || startX >= box.clientWidth - 34;
      if (isVertical() && edgeSwipe && Math.abs(moveX) > 48 && Math.abs(moveX) > Math.abs(moveY) * 1.25) {
        const forward = r.settings.direction === 'rtl' ? -1 : 1;
        stepPage((moveX < 0 ? 1 : -1) * forward);
        readerToast(moveX < 0 ? '下一页' : '上一页');
        resetSwipe();
        return;
      }

      if (swiping && !isVertical()) {
        const velocity = Math.abs(dx) / Math.max(1, elapsed);
        const forward = r.settings.direction === 'rtl' ? -1 : 1;
        const node = canvas();
        node.classList.remove('is-swiping');
        node.classList.add('is-snapping');
        if (Math.abs(dx) > box.clientWidth * 0.22 || velocity > 0.55) {
          const direction = Math.sign(dx) * forward;
          node.style.transform = `translate3d(${-direction * box.clientWidth}px, 0, 0)`;
          node.style.opacity = '.35';
          setTimeout(() => { node.style.opacity = ''; stepPage(direction); resetSwipe(); }, reducedMotion() ? 0 : 180);
          return;
        }
        node.style.transform = '';
        setTimeout(resetSwipe, 260);
        return;
      }

      resetSwipe();
      if (!isVertical() && Math.abs(moveY) > 76 && Math.abs(moveX) < 54 && elapsed < 700) {
        stepChapter(moveY < 0 ? 1 : -1);
        return;
      }
      if (Math.abs(moveX) > 12 || Math.abs(moveY) > 12 || elapsed > 420) return;

      /* 轻点：中间切换工具栏，两侧翻页（竖向滚动模式下只切换工具栏）。 */
      const now = performance.now();
      if (now - r.lastTap < 300) {
        r.lastTap = 0;
        if (isVertical()) {
          r.settings.fit = r.settings.fit === 'width' ? 'comfortable' : 'width';
          syncSettings();
          saveSettings();
          const keep = r.page, offset = r.offset;
          renderPages();
          requestAnimationFrame(() => jump(keep, offset, false));
          readerToast(r.settings.fit === 'width' ? '已切换为适应宽度' : '已切换为舒适宽度');
        } else {
          setZoom(r.zoom === 100 ? 200 : 100);
          saveSettings();
        }
        return;
      }
      r.lastTap = now;

      const ratio = touch.clientX / Math.max(1, box.clientWidth);
      if (isVertical() || (ratio > 0.28 && ratio < 0.72)) {
        setImmersive(!r.immersive);
        return;
      }
      const forward = r.settings.direction === 'rtl' ? -1 : 1;
      stepPage(ratio <= 0.28 ? -1 * forward : 1 * forward);
    };

    box.addEventListener('touchend', finish, { passive: true });
    box.addEventListener('touchcancel', () => {
      pinchStart = 0;
      resetSwipe();
      $('#readerZoomBadge').hidden = true;
    }, { passive: true });

    box.addEventListener('scroll', () => {
      if (!r.active) return;
      cancelAnimationFrame(r.scrollFrame);
      r.scrollFrame = requestAnimationFrame(() => {
        if (isVertical()) updateVisible();
        else if (r.immersive !== false) { /* 单页/双页模式不做滚动跟踪 */ }
      });
    }, { passive: true });

    /* 鼠标 / 触控板用户（平板带键盘、桌面浏览器调试） */
    box.addEventListener('click', event => {
      if (event.detail !== 0 && matchMedia('(pointer: coarse)').matches) return;
      const ratio = event.clientX / Math.max(1, box.clientWidth);
      if (isVertical() || (ratio > 0.28 && ratio < 0.72)) { setImmersive(!r.immersive); return; }
      stepPage(ratio <= 0.28 ? -1 : 1);
    });

    document.addEventListener('keydown', event => {
      if (!r.active) return;
      const typing = /^(INPUT|SELECT|TEXTAREA)$/.test(event.target.tagName);
      if (event.key === 'Escape') { event.preventDefault(); handleBack(); return; }
      if (typing || event.ctrlKey || event.altKey || event.metaKey) return;
      if (event.key === 'ArrowRight') { event.preventDefault(); stepPage(r.settings.direction === 'rtl' ? -1 : 1); }
      else if (event.key === 'ArrowLeft') { event.preventDefault(); stepPage(r.settings.direction === 'rtl' ? 1 : -1); }
      else if (event.key === 'ArrowDown' || event.key === 'PageDown' || event.key === ' ') { event.preventDefault(); isVertical() ? viewport().scrollBy({ top: viewport().clientHeight * 0.85, behavior: 'smooth' }) : stepPage(1); }
      else if (event.key === 'ArrowUp' || event.key === 'PageUp') { event.preventDefault(); isVertical() ? viewport().scrollBy({ top: -viewport().clientHeight * 0.85, behavior: 'smooth' }) : stepPage(-1); }
      else if (event.key.toLowerCase() === 'f') { event.preventDefault(); setImmersive(!r.immersive); }
      else if (event.key.toLowerCase() === 'o') { event.preventDefault(); $('#readerOptionsButton').click(); }
      else if (event.key.toLowerCase() === 'j') { event.preventDefault(); stepChapter(1); }
      else if (event.key.toLowerCase() === 'k') { event.preventDefault(); stepChapter(-1); }
    }, true);
  }

  /* ───────────────────────── 绑定 ───────────────────────── */

  function bind() {
    bindGestures();

    $('#readerBack').onclick = () => close();
    $('#readerThemeButton').onclick = () => { r.adapter.toggleTheme(); r.settings.theme = document.documentElement.dataset.theme; saveSettings(); };

    $('#readerDirectoryButton').onclick = () => {
      if ($('#readerDirectory').hidden) { r.adapter.closeSheets(); openDrawer(); } else closeDrawer();
    };
    $('#readerDirectoryClose').onclick = closeDrawer;
    $('#readerDrawerScrim').onclick = closeDrawer;
    $('#readerDirectory').onclick = event => {
      const button = event.target.closest('[data-reader-chapter]');
      if (!button) return;
      closeDrawer();
      loadChapter(button.dataset.readerChapter, Number(button.dataset.readerPage || 0));
    };

    canvas().onclick = event => {
      const button = event.target.closest('[data-retry-page]');
      if (!button || !r.context) return;
      const index = Number(button.dataset.retryPage);
      const state = r.context.pages.get(index);
      if (state) state.status = 'idle';
      r.context.allowed.add(index);
      pump(r.context);
    };

    $('#readerPrevChapter').onclick = () => stepChapter(-1);
    $('#readerNextChapter').onclick = () => stepChapter(1);
    $('#readerPrevPage').onclick = () => stepPage(-1);
    $('#readerNextPage').onclick = () => stepPage(1);
    $('#readerProgress').oninput = event => jump(Number(event.target.value));
    $('#readerProgress').onchange = () => saveProgress(true);

    $('#readerOptionsButton').onclick = () => {
      closeDrawer();
      r.adapter.openSheet($('#readerOptions'));
      cacheStats();
    };
    $('#readerOptionsClose').onclick = () => r.adapter.closeTopSheet();
    $('#readerOptionsDone').onclick = () => r.adapter.closeTopSheet();

    $('#readerDownload').onclick = () => {
      if (!r.book) return;
      r.adapter.addDownload(r.book);
      r.adapter.toast('已加入下载清单', r.book.title);
    };

    [['readerMode', 'mode'], ['readerFit', 'fit'], ['readerDirection', 'direction'], ['readerPrefetch', 'prefetch'], ['readerCacheBudget', 'cache_mb']].forEach(([id, key]) => {
      $(`#${id}`).onchange = event => {
        r.settings[key] = (key === 'prefetch' || key === 'cache_mb') ? Number(event.target.value) : event.target.value;
        saveSettings();
        if (key === 'cache_mb') { setTimeout(cacheStats, 400); return; }
        if (r.chapter) {
          const keep = r.page;
          renderPages();
          requestAnimationFrame(() => jump(keep, r.offset, false));
        }
      };
    });

    $('#readerZoom').oninput = event => { setZoom(Number(event.target.value), false); $('#readerZoomValue').textContent = `${r.zoom}%`; };
    $('#readerZoom').onchange = () => { showZoomBadge(`${r.zoom}%`); saveSettings(); };

    $('#readerClearCache').onclick = async () => {
      try {
        await api('/api/reader/cache', { method: 'DELETE' });
        await cacheStats();
        r.adapter.toast('图片缓存已清理', '不会删除下载文件、阅读进度或书签');
      } catch (error) { r.adapter.toast('缓存清理失败', error.message, 'error'); }
    };

    $('#shelfRefresh').onclick = loadShelf;
    $('#shelfFilters').onclick = event => {
      const button = event.target.closest('[data-shelf-filter]');
      if (!button) return;
      r.shelfFilter = button.dataset.shelfFilter;
      $$('#shelfFilters .chip').forEach(node => node.classList.toggle('is-active', node === button));
      renderShelf();
    };
    $('#shelfList').onclick = async event => {
      if (document.body.dataset.jmLongPress === '1') { delete document.body.dataset.jmLongPress; return; }
      const openButton = event.target.closest('[data-shelf-open]');
      if (openButton) {
        open(openButton.dataset.shelfOpen, {
          chapter: openButton.dataset.chapter,
          page: openButton.dataset.page == null ? undefined : Number(openButton.dataset.page)
        });
        return;
      }
      const favorite = event.target.closest('[data-shelf-favorite]');
      const remove = event.target.closest('[data-shelf-remove]');
      try {
        if (favorite) await api(`/api/reader/favorites/${encodeURIComponent(favorite.dataset.shelfFavorite)}`, json('PUT', { favorite: favorite.dataset.favorite === '1' }));
        else if (remove) await api(`/api/reader/shelf/${encodeURIComponent(remove.dataset.shelfRemove)}`, { method: 'DELETE' });
        else return;
        await loadShelf();
      } catch (error) { r.adapter.toast('书架更新失败', error.message, 'error'); }
    };

    window.addEventListener('resize', () => {
      if (!r.active || !r.chapter || !r.context) return;
      const context = r.context, page = r.page, offset = r.offset;
      context.restoring = true;
      cancelAnimationFrame(r.scrollFrame);
      requestAnimationFrame(() => {
        if (r.context !== context) return;
        positionPage(page, offset);
        context.restoring = false;
        syncControls();
        updateWindow();
      });
    });

    document.addEventListener('visibilitychange', () => { if (document.hidden && r.active) saveProgress(true); });
    window.addEventListener('pagehide', () => { if (r.active) saveProgress(true); });
  }

  /* ───────────────────────── 对外接口 ───────────────────────── */

  function handleBack() {
    if (!r.active) return false;
    if (r.adapter.hasSheets()) { r.adapter.closeTopSheet(); return true; }
    if (!$('#readerDirectory').hidden) { closeDrawer(); return true; }
    if (r.immersive) { setImmersive(false); return true; }
    close();
    return true;
  }

  function themeChanged(preference) {
    if (r.settingsLoaded && r.settings.theme !== preference) { r.settings.theme = preference; saveSettings(); }
  }

  function setThemePreference(preference) {
    r.settings.theme = preference;
    saveSettings();
    r.adapter.applyTheme(preference);
  }

  window.JMReader = { init, open, close, loadShelf, themeChanged, setThemePreference, handleBack, isActive: () => r.active };
})();
