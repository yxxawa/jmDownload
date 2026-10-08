(() => {
  'use strict';
  const $ = selector => document.querySelector(selector);
  const defaults = {mode:'vertical', fit:'comfortable', direction:'ltr', prefetch:3, cache_mb:512, theme:'system'};
  const r = {adapter:null, active:false, book:null, session:'', chapter:null, page:0, offset:0, generation:0,
    context:null, settings:{...defaults}, bookmarks:[], fullscreen:false, zoom:100, saveTimer:0, windowTimer:0,
    scrollFrame:0, restoreView:'discover', focusElement:null, shelfFilter:'recent', shelf:null, shelfSerial:0,
    revision:0, windowRevision:0, settingsTimer:0, settingsLoaded:false};
  const esc = value => r.adapter.escapeHtml(value);
  const icon = name => r.adapter.icon(name);
  const api = (...args) => r.adapter.api(...args);
  const json = (method, value, extra={}) => ({method, body:JSON.stringify(value), ...extra});
  const clamp = (value, min, max) => Math.min(max, Math.max(min, Number(value) || 0));
  const nextRevision = () => r.revision = Math.max(Date.now() * 1000, r.revision + 1, Number(r.book?.progress?.revision || 0) + 1);
  const chapterIndex = () => r.book?.chapters?.findIndex(c => c.id === r.chapter?.id) ?? -1;
  function abortContext() {
    const context = r.context; r.context = null;
    if (!context) return;
    context.aborted = true; context.controller.abort();
    context.pages.forEach(p => { p.controller?.abort(); if (p.url) URL.revokeObjectURL(p.url); });
    clearTimeout(r.windowTimer); cancelAnimationFrame(r.scrollFrame);
  }
  function message(text, retry) {
    $('#readerCanvas').innerHTML = `<div class="reader-message">${esc(text)}${retry ? '<button id="readerChapterRetry">重新加载本章</button>' : ''}</div>`;
    if (retry) $('#readerChapterRetry').onclick = () => loadChapter(retry, r.page);
  }
  async function init(adapter) {
    r.adapter = adapter;
    initShelfActions();
    bind();
    try { r.settings = {...defaults, ...await api('/api/reader/settings')}; r.settingsLoaded=true; syncSettings(); adapter.applyTheme(r.settings.theme); }
    catch { /* Downloader remains usable if reading settings cannot be loaded. */ }
  }
  async function open(albumId, options={}) {
    if (r.active) await close(false);
    const generation = ++r.generation;
    r.restoreView = r.adapter.currentView(); r.focusElement = document.activeElement;
    r.active = true; r.book = null; r.chapter = null; r.page = 0; r.offset = 0;
    r.session = ''; $('#readerWorkspace').hidden = false; document.body.classList.add('reader-open');
    r.adapter.closeModals(); r.adapter.closePlanner();
    $('#readerBookTitle').textContent = '正在打开作品…'; $('#readerChapterTitle').textContent = '';
    $('#readerOptions').hidden = true; $('#readerDirectory').hidden = true;
    $('#readerWorkspace').classList.remove('is-focus'); $('#readerReveal').hidden = true;
    message('正在读取作品与章节目录…'); syncControls();
    try {
      const data = await api('/api/reader/sessions', json('POST', {album_id:String(albumId)}));
      if (!r.active || generation !== r.generation) { if (data.session_id) api(`/api/reader/sessions/${data.session_id}`, {method:'DELETE'}).catch(()=>{}); return; }
      if (!data.book?.chapters?.length || !data.session_id) throw new Error('作品没有可读取的章节');
      r.session = data.session_id; r.book = data.book; r.bookmarks = data.bookmarks || [];
      r.settings = {...defaults, ...data.settings}; syncSettings(); renderDirectory();
      $('#readerBookTitle').textContent = r.book.title; $('#readerBookTitle').title = r.book.title;
      const progress = !options.fromStart ? r.book.progress : null;
      const chapter = options.chapter || progress?.chapter_id || r.book.chapters[0].id;
      const chosen = r.book.chapters.some(c => c.id === chapter) ? chapter : r.book.chapters[0].id;
      await loadChapter(chosen, options.page ?? progress?.page ?? 0, options.page == null ? progress?.offset || 0 : 0);
    } catch (error) {
      if (!r.active || generation !== r.generation) return;
      message(`暂时无法打开作品：${error.message}`);
      r.adapter.toast('阅读打开失败', error.message, 'error');
    }
  }
  async function close(returnToWorkspace=true) {
    if (!r.active) return;
    const pending = saveProgress(true), session = r.session;
    r.active = false; ++r.generation; abortContext(); clearTimeout(r.saveTimer);
    $('#readerWorkspace').hidden = true; document.body.classList.remove('reader-open');
    if (r.fullscreen) await fullscreen(false).catch(()=>{});
    r.session = '';
    await pending;
    if (session) await api(`/api/reader/sessions/${session}`, {method:'DELETE'}).catch(()=>{});
    if (returnToWorkspace) { r.adapter.showView(r.restoreView); r.focusElement?.focus?.(); }
  }
  async function loadChapter(chapterId, page=0, offset=0) {
    if (!r.active || !r.session) return;
    if (r.chapter) await saveProgress(true);
    abortContext();
    const generation = ++r.generation;
    const context = {generation, controller:new AbortController(), pages:new Map(), slots:[], allowed:new Set(), pending:0, aborted:false, restoring:true};
    r.context = context; r.chapter = null;
    $('#readerChapterTitle').textContent = r.book.chapters.find(c => c.id === chapterId)?.title || '';
    message('正在加载当前章节…'); syncControls();
    try {
      const chapter = await api(`/api/reader/chapters/${encodeURIComponent(chapterId)}?session=${encodeURIComponent(r.session)}`, {signal:context.controller.signal});
      if (!r.active || r.context !== context) return;
      if (!chapter.page_count || !chapter.pages?.length) throw new Error('章节没有图片');
      r.chapter = chapter; r.page = clamp(page, 0, chapter.page_count - 1); r.offset = clamp(offset, 0, 1);
      $('#readerChapterTitle').textContent = `${chapter.title}${chapter.local ? ' · 本地' : ''}`;
      renderPages(context); renderDirectory(); syncControls();
      context.pendingRestore = {page:r.page, offset:r.offset};
      positionPage(r.page, r.offset);
      await updateWindow(context, true);
      requestAnimationFrame(() => {
        if (r.context !== context) return;
        jump(r.page, r.offset, false); context.restoring = false; updateVisible(); $('#readerViewport').focus();
      });
    } catch (error) {
      if (!r.active || r.context !== context || error.name === 'AbortError') return;
      message(`章节读取失败：${error.message}`, chapterId);
    }
  }
  function renderPages(context=r.context) {
    if (!context || !r.chapter) return;
    const canvas = $('#readerCanvas');
    canvas.dataset.mode = r.settings.mode; canvas.dataset.fit = r.settings.fit; canvas.dataset.direction = r.settings.direction;
    canvas.style.setProperty('--reader-zoom', String(r.zoom / 100));
    let indexes = r.settings.mode === 'vertical' ? Array.from({length:r.chapter.page_count}, (_,i)=>i) :
      r.settings.mode === 'spread' ? [r.page, r.page + 1].filter(i => i < r.chapter.page_count) : [r.page];
    canvas.innerHTML = indexes.map(i => `<div class="reader-page" data-page="${i}" data-status="idle"><div class="reader-placeholder">第 ${i+1} 页 · 等待加载</div></div>`).join('');
    context.slots = [...canvas.querySelectorAll('.reader-page')];
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
      if (slot.querySelector('img')?.src === pageState.url) return;
      slot.innerHTML = `<img alt="第 ${index+1} 页" decoding="async">`;
      slot.querySelector('img').src = pageState.url;
    } else if (pageState?.status === 'error') {
      slot.innerHTML = `<div class="reader-placeholder"><span>第 ${index+1} 页加载失败</span><small>${esc(pageState.error)}</small><button class="reader-retry" data-retry-page="${index}">重试这一页</button></div>`;
    } else {
      const label = pageState?.status === 'loading' ? '正在加载…' : '等待加载';
      if (slot.querySelector('img') || slot.dataset.label !== label) slot.innerHTML = `<div class="reader-placeholder">第 ${index+1} 页 · ${label}</div>`;
      slot.dataset.label = label;
    }
  }
  function visibleRange() {
    if (!r.context || !r.chapter) return [r.page, r.page];
    if (r.settings.mode !== 'vertical') return [r.page, Math.min(r.chapter.page_count-1, r.page + (r.settings.mode==='spread' ? 1 : 0))];
    const viewport = $('#readerViewport');
    const top = viewport.getBoundingClientRect().top;
    const bottom = top + viewport.clientHeight;
    const visible = r.context.slots.filter(slot => {const rect = slot.getBoundingClientRect(); return rect.bottom > top + 2 && rect.top < bottom - 2;});
    if (!visible.length) return [r.page,r.page];
    return [Number(visible[0].dataset.page),Number(visible[visible.length-1].dataset.page)];
  }
  async function updateWindow(context=r.context, immediate=false) {
    if (!context || r.context !== context || !r.chapter) return;
    const [start,end] = visibleRange();
    const first = Math.max(0,start-1), last = Math.min(r.chapter.page_count-1,end + Number(r.settings.prefetch));
    context.allowed = new Set(Array.from({length:last-first+1}, (_,i)=>i+first));
    // Keep only a small page window decoded in browser memory. Empty slots preserve scroll height.
    context.pages.forEach((state,i) => {
      if (context.allowed.has(i)) return;
      state.controller?.abort();
      if (state.url) {URL.revokeObjectURL(state.url); state.url = '';}
      if (state.status !== 'error') state.status = 'idle';
      const slot = context.slots.find(n => Number(n.dataset.page)===i); if (slot) paint(slot,state);
    });
    clearTimeout(r.windowTimer);
    const chapter = r.chapter.id, session = r.session, revision = ++r.windowRevision;
    const announce = () => api(`/api/reader/sessions/${session}/window`, json('PATCH', {chapter_id:chapter,start,end,revision})).catch(()=>{});
    if (immediate) await announce(); else r.windowTimer = setTimeout(announce, 100);
    pump(context);
  }
  function pump(context) {
    if (context.aborted || r.context !== context || !r.chapter) return;
        const current = visibleRange();
    const visible = new Set(Array.from({length:current[1]-current[0]+1},(_,n)=>current[0]+n));
    const wanted = [...context.allowed].sort((a,b) =>
      (visible.has(b) ? 1 : 0) - (visible.has(a) ? 1 : 0) ||
      Math.abs(a-r.page) - Math.abs(b-r.page));
    for (const i of wanted) {
      if (context.pending >= 5) break;
      const old = context.pages.get(i);
      if (old && old.status !== 'idle') continue;
      loadPage(context,i);
    }
  }
  async function loadPage(context, index) {
    const state = context.pages.get(index) || {status:'idle'};
    state.status='loading'; state.controller=new AbortController(); context.pages.set(index,state); context.pending++;
    const controller = state.controller;
    const source = r.chapter.pages[index].url;
    const slot = () => context.slots.find(n => Number(n.dataset.page) === index);
    if (slot()) paint(slot(),state);
    try {
      const url = new URL(source,location.href);
      if (r.adapter.token) url.searchParams.set('token',r.adapter.token);
      const response = await fetch(url.pathname+url.search, {signal:controller.signal});
      if (!response.ok) { let payload; try {payload=await response.json();} catch {} throw new Error(payload?.detail || `图片请求失败 (${response.status})`); }
      const blob = await response.blob();
      if (context.aborted || r.context !== context || controller.signal.aborted) return;
      const objectUrl = URL.createObjectURL(blob);
      const image = new Image(); image.src=objectUrl;
      try { await image.decode(); } catch { URL.revokeObjectURL(objectUrl); throw new Error('图片格式无效或未正确还原'); }
      if (context.aborted || r.context !== context || controller.signal.aborted) {URL.revokeObjectURL(objectUrl); return;}
      state.url=objectUrl; state.width=image.naturalWidth; state.height=image.naturalHeight; state.status='ready';
      // Preserve the current anchor when a preceding placeholder gets its real aspect ratio.
      const viewport = $('#readerViewport'); const current = context.slots.find(n => Number(n.dataset.page)===r.page);
      const anchorBefore = current?.getBoundingClientRect().top;
      if (slot()) paint(slot(),state);
      if (r.settings.mode === 'vertical' && current && anchorBefore != null && index < r.page) viewport.scrollTop += current.getBoundingClientRect().top-anchorBefore;
      if (context.pendingRestore?.page === index && r.page === index) { const target=context.pendingRestore; context.pendingRestore=null; jump(target.page,target.offset,false); }
    } catch (error) {
      if (context.aborted || r.context !== context) return;
      if (error.name === 'AbortError') state.status='idle';
      else {state.status='error'; state.error=error.message; if (slot()) paint(slot(),state);}
    } finally {
      if (state.controller===controller) state.controller=null;
      context.pending--;
      if (r.context===context && !context.aborted) { pump(context); }
    }
  }
  function updateVisible() {
    if (!r.active || !r.chapter || !r.context || r.context.restoring) return;
    if (r.settings.mode==='vertical') {
      const [start] = visibleRange(); r.page=start;
      const slot=r.context.slots.find(n=>Number(n.dataset.page)===start);
      if (slot) r.offset=clamp(($('#readerViewport').getBoundingClientRect().top-slot.getBoundingClientRect().top)/slot.getBoundingClientRect().height,0,1);
    }
    syncControls(); updateWindow(); scheduleSave();
  }
  function positionPage(page, offset=0) {
    const viewport=$('#readerViewport'),slot=r.context?.slots.find(n=>Number(n.dataset.page)===page);
    if (slot && r.settings.mode==='vertical') viewport.scrollTop += slot.getBoundingClientRect().top-viewport.getBoundingClientRect().top+slot.getBoundingClientRect().height*offset;
  }
  function jump(page, offset=0, save=true) {
    if (!r.chapter || !r.context) return;
    r.page=clamp(page,0,r.chapter.page_count-1); r.offset=offset;
    if(save)r.context.pendingRestore=null;
    if (r.settings.mode==='vertical') {
      positionPage(r.page,offset);
    } else { renderPages(); $('#readerViewport').scrollTo(0,0); }
    syncControls(); updateWindow(); if (save) scheduleSave();
  }
  function stepPage(direction) {
    if (!r.chapter) return;
    const step=r.settings.mode==='spread'?2:1;
    const page=r.page + direction*step;
    if (page<0) stepChapter(-1,true); else if (page>=r.chapter.page_count) stepChapter(1); else jump(page);
  }
  function stepChapter(direction, last=false) {
    const i=chapterIndex(), next=r.book?.chapters?.[i+direction];
    if (next) loadChapter(next.id,last?100000:0);
  }
  function syncControls() {
    const count=r.chapter?.page_count||0, i=chapterIndex();
    $('#readerPageInput').value=count?r.page+1:1; $('#readerPageInput').max=Math.max(1,count); $('#readerPageInput').disabled=!count;
    $('#readerPageTotal').textContent=`/ ${count||'—'}`;
    $('#readerProgress').max=Math.max(0,count-1); $('#readerProgress').value=r.page; $('#readerProgress').disabled=!count;
    $('#readerPrevChapter').disabled=i<=0; $('#readerNextChapter').disabled=i<0 || i>=((r.book?.chapters?.length||0)-1);
    $('#readerPrevPage').disabled=!count || (r.page===0 && i<=0);
    $('#readerNextPage').disabled=!count || (r.page>=count-1 && i>=r.book.chapters.length-1);
    const marked=r.bookmarks.some(b=>b.chapter_id===r.chapter?.id && b.page===r.page);
    $('#readerBookmarkButton').classList.toggle('is-active',marked); $('#readerBookmarkButton').setAttribute('aria-pressed',String(marked));
    $('#readerBookmarkButton').disabled=!count; $('#readerDownload').disabled=!r.book;
  }
  function syncSettings() {
    ['Mode','Fit','Direction','Prefetch','CacheBudget'].forEach((suffix,i)=>{
      const key=['mode','fit','direction','prefetch','cache_mb'][i]; const select=$(`#reader${suffix}`);
      const value=String(r.settings[key]); if (![...select.options].some(o=>o.value===value)) select.add(new Option(value,value)); select.value=value;
    });
  }
  function scheduleSave() { clearTimeout(r.saveTimer); r.saveTimer=setTimeout(()=>saveProgress(),650); }
  async function saveProgress(immediate=false) {
    clearTimeout(r.saveTimer);
    if (!r.book || !r.chapter) return;
    const id=r.book.id, progress={chapter_id:r.chapter.id,page:r.page,offset:r.offset,revision:nextRevision()};
    r.book.progress=progress;
    try {
      await api(`/api/reader/progress/${encodeURIComponent(id)}`,json('PUT',progress,{keepalive:immediate}));
      if (r.book?.id===id) $('#readerSaveStatus').textContent='进度已保存';
    } catch { if (r.book?.id===id) $('#readerSaveStatus').textContent='进度未同步'; }
  }
  function saveSettings() {
    clearTimeout(r.settingsTimer);
    r.settingsTimer=setTimeout(()=>api('/api/reader/settings',json('PUT',r.settings)).catch(e=>r.adapter.toast('阅读设置保存失败',e.message,'error')),250);
  }
  function renderDirectory() {
    if (!r.book) return;
    $('#readerChapters').innerHTML=r.book.chapters.map(c=>`<button data-reader-chapter="${esc(c.id)}" class="${c.id===r.chapter?.id?'is-current':''}">${esc(c.sort||'')} · ${esc(c.title)}</button>`).join('');
    $('#readerBookmarks').innerHTML=r.bookmarks.length?r.bookmarks.map(b=>`<button data-reader-chapter="${esc(b.chapter_id)}" data-reader-page="${b.page}">${esc(r.book.chapters.find(c=>c.id===b.chapter_id)?.title||'章节')} · 第 ${b.page+1} 页</button>`).join(''):'<div class="muted">还没有书签</div>';
  }
  async function bookmark() {
    if (!r.chapter || !r.book) return;
    const value={album_id:r.book.id,chapter_id:r.chapter.id,page:r.page};
    try {
      const result=await api('/api/reader/bookmarks',json('POST',value));
      r.bookmarks=r.bookmarks.filter(b=>!(b.chapter_id===value.chapter_id&&b.page===value.page));
      if (result.bookmarked) r.bookmarks.push(value);
      syncControls(); renderDirectory(); r.adapter.toast(result.bookmarked?'已添加书签':'已移除书签',`第 ${value.page+1} 页`);
    } catch(e) { r.adapter.toast('书签保存失败',e.message,'error'); }
  }
  async function fullscreen(enabled=!r.fullscreen) {
    if (r.adapter.desktopMode) {
      const result=await r.adapter.bridgeCall('setFullscreen',{enabled}); r.fullscreen=!!result.fullscreen;
    } else { if (enabled) await document.documentElement.requestFullscreen(); else if(document.fullscreenElement) await document.exitFullscreen(); r.fullscreen=!!document.fullscreenElement; }
    $('#readerFullscreenButton').classList.toggle('is-active',r.fullscreen);
  }
  function focus(enabled=!$('#readerWorkspace').classList.contains('is-focus')) {
    $('#readerWorkspace').classList.toggle('is-focus',enabled); $('#readerReveal').hidden=!enabled;
    if(enabled) {$('#readerOptions').hidden=true;$('#readerDirectory').hidden=true;}
  }
  async function cacheStats() {
    try {const stats=await api('/api/reader/cache');$('#readerCacheStats').textContent=`${(Number(stats.bytes||0)/1048576).toFixed(1)} MB / ${Math.round(Number(stats.budget_bytes||0)/1048576)} MB`;}
    catch {$('#readerCacheStats').textContent='缓存信息暂不可用';}
  }
  async function loadShelf() {
    const serial=++r.shelfSerial;
    if(!r.shelf) $('#shelfList').innerHTML='<div class="empty-state"><p>正在读取书架…</p></div>';
    try { const data=await api('/api/reader/shelf');if(serial!==r.shelfSerial)return;r.shelf={books:data.books||[],bookmarks:data.bookmarks||[]};renderShelf();r.adapter.onShelfChanged?.(); }
    catch(e) {if(serial!==r.shelfSerial)return;if(!r.shelf)$('#shelfList').innerHTML=`<div class="empty-state"><strong>书架读取失败</strong><p>${esc(e.message)}</p></div>`;else r.adapter.toast('书架更新失败',e.message,'error');}
  }

  let shelfBulk;
  const shelfKey = book => book.mark ? [book.mark.album_id, book.mark.chapter_id, book.mark.page].join('|') : String(book.id);
  function shelfRows() {
    if (!r.shelf) return [];
    if (r.shelfFilter === 'bookmarks') return r.shelf.bookmarks.map(mark => ({ ...r.shelf.books.find(book => book.id === mark.album_id), mark })).filter(book => book.id);
    return r.shelf.books.filter(book => r.shelfFilter === 'favorite' ? book.favorite : r.shelfFilter === 'local' ? book.local : !book.hidden_from_recent);
  }
  function shelfRemoveLabel() { return r.shelfFilter === 'favorite' ? '取消收藏' : r.shelfFilter === 'local' ? '移出选中' : '删除选中'; }
  function shelfRemoveMessage(count) {
    if (r.shelfFilter === 'favorite') return '取消选中的 ' + count + ' 个收藏？阅读记录和下载文件会保留。';
    if (r.shelfFilter === 'bookmarks') return '删除选中的 ' + count + ' 个书签？其他书签和阅读记录会保留。';
    if (r.shelfFilter === 'local') return '将选中的 ' + count + ' 个作品移出本地书架？磁盘上的下载文件会保留。';
    return '删除选中的 ' + count + ' 条最近阅读记录？收藏、书签与下载文件会保留。';
  }
  async function removeShelfRows(rows) {
    const action = { recent:'history', favorite:'unfavorite', bookmarks:'bookmarks', local:'local' }[r.shelfFilter];
    await api('/api/reader/manage', json('POST', { action, ids:rows.filter(book => !book.mark).map(book => String(book.id)), bookmarks:rows.filter(book => book.mark).map(book => book.mark) }));
    await loadShelf();
    r.adapter.toast('书架已更新', '已处理 ' + rows.length + ' 项');
  }
  function initShelfActions() {
    shelfBulk = window.JMBulk.create({ list:'#shelfList', row:'.shelf-item', key:shelfKey,
      title:book => book.title + (book.mark ? ' 第 ' + (Number(book.mark.page) + 1) + ' 页书签' : ''),
      removeLabel:shelfRemoveLabel, confirmLabel:() => r.shelfFilter === 'favorite' ? '取消收藏' : r.shelfFilter === 'local' ? '移出' : '删除',
      message:shelfRemoveMessage, remove:removeShelfRows, error:error => r.adapter.toast('书架更新失败', error.message, 'error') });
  }

  function renderShelf() {
    if(!r.shelf)return;
    const rows = shelfRows();
    $('#shelfCount').textContent=`${rows.length} 项`;
    $('#shelfList').innerHTML=rows.length?rows.map(b=>{
      const progress=b.mark||b.progress, chapter=b.chapters?.find(c=>c.id===progress?.chapter_id);
      const description=progress?`${chapter?.title||'章节'} · 第 ${Number(progress.page)+1} 页`:'尚未阅读';
      return `<article class="shelf-item"><img class="shelf-cover" src="${r.adapter.coverUrl(b.id)}" alt="" loading="lazy" onerror="this.style.visibility='hidden'"><div class="shelf-copy"><strong>${esc(b.title)}</strong><p>${esc(description)}${b.local?' · 本地可读':''}</p></div><div class="shelf-actions"><button class="shelf-read" data-shelf-open="${esc(b.id)}" ${b.mark?`data-chapter="${esc(b.mark.chapter_id)}" data-page="${b.mark.page}"`:''}>${progress?'继续阅读':'开始阅读'}</button><button data-shelf-favorite="${esc(b.id)}" data-favorite="${b.favorite?'0':'1'}" class="${b.favorite?'is-favorite':''}" title="${b.favorite?'取消收藏':'收藏'}" aria-label="${b.favorite?'取消收藏':'收藏'}"><svg aria-hidden="true"><use href="#i-star"/></svg></button><button data-shelf-remove="${esc(shelfKey(b))}" title="${shelfRemoveLabel()}" aria-label="${shelfRemoveLabel()}">${icon('x')}</button></div></article>`;
    }).join(''):`<div class="empty-state"><strong>${r.shelfFilter==='local'?'还没有已完成的本地作品':'这里暂时没有内容'}</strong><p>${r.shelfFilter==='local'?'完整的图片下载会自动登记到这里，不把未完成目录当成离线作品。':'从作品详情开始阅读，进度和书签会保存在本机。'}</p></div>`;
    shelfBulk?.refresh(rows, r.shelfFilter);
  }
  function bind() {
    $('#readerBack').onclick=()=>close();
    $('#readerDirectoryButton').onclick=()=>{$('#readerDirectory').hidden=!$('#readerDirectory').hidden;};$('#readerDirectoryClose').onclick=()=>{$('#readerDirectory').hidden=true;};
    $('#readerDirectory').onclick=e=>{const b=e.target.closest('[data-reader-chapter]');if(b)loadChapter(b.dataset.readerChapter,Number(b.dataset.readerPage||0));};
    $('#readerCanvas').onclick=e=>{const b=e.target.closest('[data-retry-page]');if(b&&r.context){const i=Number(b.dataset.retryPage),state=r.context.pages.get(i);if(state)state.status='idle';r.context.allowed.add(i);pump(r.context);}};
    $('#readerPrevChapter').onclick=()=>stepChapter(-1);$('#readerNextChapter').onclick=()=>stepChapter(1);$('#readerPrevPage').onclick=()=>stepPage(-1);$('#readerNextPage').onclick=()=>stepPage(1);
    $('#readerPageInput').onchange=e=>jump(Number(e.target.value)-1);$('#readerProgress').oninput=e=>jump(Number(e.target.value));
    $('#readerBookmarkButton').onclick=bookmark;$('#readerThemeButton').onclick=()=>{r.adapter.toggleTheme();r.settings.theme=document.documentElement.dataset.theme;saveSettings();};
    $('#readerFocusButton').onclick=()=>focus();$('#readerReveal').onclick=()=>focus(false);
    $('#readerFullscreenButton').onclick=()=>fullscreen().catch(e=>r.adapter.toast('全屏切换失败',e.message,'error'));
    $('#readerOptionsButton').onclick=()=>{$('#readerOptions').hidden=!$('#readerOptions').hidden;if(!$('#readerOptions').hidden)cacheStats();};$('#readerOptionsClose').onclick=()=>{$('#readerOptions').hidden=true;};
    ['Mode','Fit','Direction','Prefetch','CacheBudget'].forEach((suffix,i)=>$(`#reader${suffix}`).onchange=e=>{
      const key=['mode','fit','direction','prefetch','cache_mb'][i];r.settings[key]=i>=3?Number(e.target.value):e.target.value;saveSettings();
      if(r.chapter){renderPages();requestAnimationFrame(()=>jump(r.page,r.offset,false));}
      if(suffix==='CacheBudget')setTimeout(cacheStats,400);
    });
    $('#readerZoom').oninput=e=>{r.zoom=Number(e.target.value);$('#readerZoomValue').textContent=`${r.zoom}%`;$('#readerCanvas').style.setProperty('--reader-zoom',String(r.zoom/100));requestAnimationFrame(()=>jump(r.page,r.offset,false));};
    $('#readerClearCache').onclick=async()=>{try{await api('/api/reader/cache',{method:'DELETE'});await cacheStats();r.adapter.toast('图片缓存已清理','不会删除下载文件、阅读进度或书签');}catch(e){r.adapter.toast('缓存清理失败',e.message,'error');}};
    $('#readerDownload').onclick=()=>{if(r.book){r.adapter.addDownload(r.book);r.adapter.toast('已加入下载清单',r.book.title);}};
    $('#readerViewport').onscroll=()=>{cancelAnimationFrame(r.scrollFrame);r.scrollFrame=requestAnimationFrame(updateVisible);};
    $('#shelfRefresh').onclick=loadShelf;
    $('#shelfFilters').onclick=e=>{const b=e.target.closest('[data-shelf-filter]');if(!b)return;r.shelfFilter=b.dataset.shelfFilter;document.querySelectorAll('[data-shelf-filter]').forEach(n=>n.classList.toggle('is-active',n===b));renderShelf();};
    $('#shelfList').onclick=async e=>{
      const openButton=e.target.closest('[data-shelf-open]');if(openButton){open(openButton.dataset.shelfOpen,{chapter:openButton.dataset.chapter,page:openButton.dataset.page==null?undefined:Number(openButton.dataset.page)});return;}
      const favorite=e.target.closest('[data-shelf-favorite]'),remove=e.target.closest('[data-shelf-remove]');
      try{if(favorite)await api(`/api/reader/favorites/${favorite.dataset.shelfFavorite}`,json('PUT',{favorite:favorite.dataset.favorite==='1'}));else if(remove){const row=shelfRows().find(book=>shelfKey(book)===remove.dataset.shelfRemove);if(row&&await window.JMBulk.confirm(shelfRemoveMessage(1),shelfRemoveLabel()))await removeShelfRows([row]);return;}else return;await loadShelf();}catch(error){r.adapter.toast('书架更新失败',error.message,'error');}
    };
    document.addEventListener('keydown',e=>{
      if(!r.active)return;
      const typing=/^(INPUT|SELECT|TEXTAREA)$/.test(e.target.tagName);
      if(e.key==='Escape') {e.preventDefault();e.stopImmediatePropagation();if(r.fullscreen)fullscreen(false).catch(()=>{});else if(!$('#readerOptions').hidden)$('#readerOptions').hidden=true;else if(!$('#readerDirectory').hidden)$('#readerDirectory').hidden=true;else if($('#readerWorkspace').classList.contains('is-focus'))focus(false);else close();return;}
      if(e.key==='F11'){e.preventDefault();e.stopImmediatePropagation();fullscreen().catch(()=>{});return;}
      if(typing||e.ctrlKey||e.altKey||e.metaKey)return;
      if(e.key==='ArrowRight'){e.preventDefault();stepPage(r.settings.direction==='rtl'?-1:1);}else if(e.key==='ArrowLeft'){e.preventDefault();stepPage(r.settings.direction==='rtl'?1:-1);}else if(e.key==='PageDown'&&r.settings.mode!=='vertical'){e.preventDefault();stepPage(1);}else if(e.key==='PageUp'&&r.settings.mode!=='vertical'){e.preventDefault();stepPage(-1);}else if(e.key.toLowerCase()==='c')$('#readerDirectoryButton').click();else if(e.key.toLowerCase()==='b')bookmark();else if(e.key==='Tab'&&e.target===$('#readerViewport')){e.preventDefault();focus();}
    },true);
    document.addEventListener('fullscreenchange',()=>{if(!r.adapter.desktopMode){r.fullscreen=!!document.fullscreenElement;$('#readerFullscreenButton').classList.toggle('is-active',r.fullscreen);}});
    window.addEventListener('jm-native-fullscreen',e=>{r.fullscreen=!!e.detail?.fullscreen;$('#readerFullscreenButton').classList.toggle('is-active',r.fullscreen);});
    window.addEventListener('resize',()=>{
      if(!r.active||!r.chapter||!r.context)return;
      const context=r.context,page=r.page,offset=r.offset;
      context.restoring=true;cancelAnimationFrame(r.scrollFrame);
      requestAnimationFrame(()=>{if(r.context!==context)return;positionPage(page,offset);context.restoring=false;syncControls();updateWindow();});
    });
    document.addEventListener('visibilitychange',()=>{if(document.hidden&&r.active)saveProgress(true);});window.addEventListener('pagehide',()=>{if(r.active)saveProgress(true);});
  }
  function themeChanged(preference){if(r.settingsLoaded&&r.settings.theme!==preference){r.settings.theme=preference;saveSettings();}}
  window.JMReader={init,open,close,loadShelf,themeChanged,isActive:()=>r.active};
})();
