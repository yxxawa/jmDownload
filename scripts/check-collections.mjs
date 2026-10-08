import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, resolve, extname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
let chromium;
try { ({ chromium } = require('playwright')); }
catch { ({ chromium } = require(resolve(root, '.ui-test/node_modules/playwright'))); }
const server = createServer(async (request, response) => {
  try {
    const pathname = new URL(request.url, 'http://local').pathname;
    const path = resolve(root, '.' + pathname);
    if (!path.startsWith(root + sep)) { response.writeHead(403).end(); return; }
    const bytes = await readFile(path);
    const types = { '.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.png':'image/png', '.svg':'image/svg+xml' };
    response.writeHead(200, { 'Content-Type':types[extname(path)] || 'application/octet-stream' }); response.end(bytes);
  } catch { response.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = 'http://127.0.0.1:' + server.address().port;
const browser = await chromium.launch({ channel:process.env.JM_TEST_BROWSER || 'msedge', headless:true });
const output = resolve(root, 'artifacts/collection-ui');
await mkdir(output, { recursive:true });
const results = [];
try {
  for (const client of ['desktop','mobile']) {
    console.log('Checking ' + client + ' collection interactions');
    const fixture = {
      books:['101','102','103'].map(id => ({ id, title:'测试作品 ' + id, chapters:[{ id:id + '1', title:'第一章', sort:1 }], favorite:id === '101', local:id === '101', hidden_from_recent:false, progress:{ chapter_id:id + '1', page:0 } })),
      bookmarks:[{ album_id:'101', chapter_id:'1011', page:0 }, { album_id:'101', chapter_id:'1011', page:1 }],
      snapshot:{ run_id:'fixture-run', running:true, stopping:false, tasks:[{ item_id:'101', status:'success', base_dir:'Fixture folder' }, { item_id:'102', status:'failed', base_dir:'Fixture folder' }, { item_id:'103', status:'queued', base_dir:'Fixture folder' }], last_success_ids:['101'], last_failed_ids:['102'] },
      failFavorite:true, manageRequests:[], favoriteRequests:[], removedTasks:[]
    };
    const context = await browser.newContext({ viewport:client === 'mobile' ? { width:390, height:844 } : { width:1440, height:900 }, reducedMotion:'reduce' });
    await context.addInitScript(() => {
      if (localStorage.getItem('collection-fixture')) return;
      localStorage.setItem('collection-fixture','1');
      localStorage.setItem('jm-motion-v1','"reduced"');
      localStorage.setItem('jm-theme-v2','"light"');
      localStorage.setItem('jm-recent-v2',JSON.stringify(['测试搜索 A','测试搜索 B']));
      localStorage.setItem('jm-logs-v2',JSON.stringify([{ level:'INFO', message:'测试活动 A', time:'2026-10-08T00:00:00Z' },{ level:'INFO', message:'测试活动 B', time:'2026-10-08T00:01:00Z' }]));
      localStorage.setItem('jm-history-v2',JSON.stringify(['101','102','103'].map(id => ({ id, title:'测试作品 ' + id, status:id === '102' ? 'failed' : 'success', path:'Fixture folder', time:'2026-10-08T00:00:00Z' }))));
    });
    await context.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname;
      const body = route.request().postData() ? route.request().postDataJSON() : {};
      let payload = {}, status = 200;
      if (path.includes('/api/cover/')) { await route.fulfill({ contentType:'image/svg+xml', body:'<svg xmlns="http://www.w3.org/2000/svg" width="120" height="160"><rect width="120" height="160" fill="#dce7df"/></svg>' }); return; }
      if (path === '/api/config') payload = { base_dir:'Fixture folder', default_base_dir:'Fixture folder', output_format:'images', image_format:'.png', pdf_mode:'merged', photo_threads:1, image_threads:5, album_threads:1, filename_lang:'traditional', auto_path:true };
      else if (path === '/api/tasks') payload = fixture.snapshot;
      else if (path === '/api/tasks/remove') {
        fixture.removedTasks = body.ids;
        if (fixture.snapshot.tasks.some(task => body.ids.includes(task.item_id) && task.status === 'queued')) { status = 400; payload = { detail:'active task' }; }
        else { fixture.snapshot.tasks = fixture.snapshot.tasks.filter(task => !body.ids.includes(task.item_id)); payload = fixture.snapshot; }
      }
      else if (path.startsWith('/api/ranking') || path.startsWith('/api/search')) payload = { items:fixture.books.map(book => ({ id:book.id, title:book.title, rank:Number(book.id) - 100 })), page:1, has_next:false };
      else if (path.startsWith('/api/album/')) { const book = fixture.books.find(book => book.id === path.split('/').pop()); payload = { ...book, author:'测试作者', tags:[], page_count:2 }; }
      else if (path.startsWith('/api/reader/albums/')) payload = fixture.books.find(book => book.id === path.split('/').pop());
      else if (path === '/api/reader/shelf') payload = { books:fixture.books, bookmarks:fixture.bookmarks };
      else if (path === '/api/reader/settings') payload = { mode:'vertical', fit:'comfortable', direction:'ltr', prefetch:3, cache_mb:512, theme:'light' };
      else if (path === '/api/reader/favorites' && route.request().method() === 'GET') payload = { ids:fixture.books.filter(book => book.favorite).map(book => book.id) };
      else if (path === '/api/reader/favorites') {
        fixture.favoriteRequests.push(body.ids);
        const failed = body.ids.filter(id => fixture.failFavorite && id === '103').map(id => ({ id, detail:'模拟失败' }));
        const succeeded = body.ids.filter(id => !failed.some(item => item.id === id));
        const changed = fixture.books.filter(book => succeeded.includes(book.id) && book.favorite !== body.favorite).map(book => book.id);
        fixture.books.forEach(book => { if (succeeded.includes(book.id)) book.favorite = body.favorite; });
        payload = { succeeded, changed, failed };
      }
      else if (path.startsWith('/api/reader/favorites/')) { const book = fixture.books.find(book => book.id === path.split('/').pop()); book.favorite = body.favorite; payload = book; }
      else if (path === '/api/reader/manage') {
        fixture.manageRequests.push(body);
        if (body.action === 'bookmarks') fixture.bookmarks = fixture.bookmarks.filter(mark => !body.bookmarks.some(item => item.album_id === mark.album_id && item.chapter_id === mark.chapter_id && item.page === mark.page));
        else fixture.books.forEach(book => { if (!body.ids.includes(book.id)) return; if (body.action === 'history') book.hidden_from_recent = true; if (body.action === 'unfavorite') book.favorite = false; if (body.action === 'local') book.local = false; });
        payload = { removed:body.action === 'bookmarks' ? body.bookmarks.length : body.ids.length };
      }
      await route.fulfill({ status, contentType:'application/json', body:JSON.stringify(payload) });
    });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    const toolbar = id => page.locator('[data-bulk-for="' + id + '"]');
    const row = (list, key) => page.locator('#' + list + ' [data-bulk-key="' + key + '"]');
    const nav = async view => page.locator((client === 'mobile' ? '.tab' : '.nav-item') + '[data-view="' + view + '"]').click();
    const count = async (selector, expected) => page.waitForFunction(({ selector, expected }) => document.querySelectorAll(selector).length === expected, { selector, expected });
    const confirm = async () => { await page.locator('.bulk-dialog button[value="confirm"]').click(); await page.locator('.bulk-dialog').waitFor({ state:'detached' }); };
    const manage = async id => toolbar(id).locator('[data-bulk-manage]').click();
    const closeOverlay = async () => { if (client === 'mobile') await page.locator('#scrim').click({ position:{ x:5, y:5 } }); else await page.keyboard.press('Escape'); };
    await page.goto(origin + (client === 'mobile' ? '/mobile/frontend/index.html' : '/frontend/index.html'));
    await count('.album-card',3);
    await page.locator('[data-detail="101"]').first().click();
    const favorite = page.locator('#detailFavoriteButton'); await favorite.waitFor();
    assert.equal(await favorite.getAttribute('aria-pressed'),'true');
    await favorite.click(); await page.waitForFunction(() => document.querySelector('#detailFavoriteButton')?.dataset.favorite === '0');
    await favorite.click(); await page.waitForFunction(() => document.querySelector('#detailFavoriteButton')?.dataset.favorite === '1');
    await closeOverlay();
    await page.locator('[data-toggle-select="101"]').first().click();
    if (await page.locator('#plannerToggleTop').isVisible()) await page.locator('#plannerToggleTop').click();
    await page.waitForFunction(() => document.querySelector('#batchFavoriteButton')?.textContent.trim() === '取消收藏');
    await page.locator('#batchFavoriteButton').click();
    await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled && document.querySelector('#batchFavoriteButton').textContent.trim() === '收藏');
    assert.equal(fixture.books[0].favorite,false);
    await page.locator('#batchFavoriteButton').click();
    await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled && document.querySelector('#batchFavoriteButton').textContent.trim() === '取消收藏');
    assert.equal(fixture.books[0].favorite,true);
    await closeOverlay();
    await page.locator('[data-toggle-select="101"]').first().click();
    await page.locator('[data-toggle-select="103"]').first().click();
    if (await page.locator('#plannerToggleTop').isVisible()) await page.locator('#plannerToggleTop').click();
    assert.equal((await page.locator('#batchFavoriteButton').textContent()).trim(),'收藏');
    await page.locator('#batchFavoriteButton').click();
    await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled && document.querySelector('#batchFavoriteButton').textContent.trim() === '取消收藏');
    assert.equal(fixture.books[2].favorite,true);
    await page.locator('#batchFavoriteButton').click();
    await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled && document.querySelector('#batchFavoriteButton').textContent.trim() === '收藏');
    await closeOverlay();
    await page.locator('[data-toggle-select="103"]').first().click();
    for (const id of ['101','102','103']) await page.locator('[data-toggle-select="' + id + '"]').first().click();
    if (await page.locator('#plannerToggleTop').isVisible()) await page.locator('#plannerToggleTop').click();
    await count('#selectedList .selected-item',3);
    await page.locator('#batchFavoriteButton').click(); await page.locator('#batchFavoriteButton').waitFor({ state:'visible' });
    await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled);
    assert.equal(fixture.books[1].favorite,true); assert.equal(fixture.books[2].favorite,false);
    assert.equal(await page.locator('#selectedList .selected-item').count(),3);
    assert.equal((await page.locator('#batchFavoriteButton').textContent()).trim(),'撤销操作');
    await page.locator('#batchFavoriteButton').click(); await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled);
    assert.deepEqual(fixture.favoriteRequests.at(-1),['102']);
    assert.deepEqual(fixture.books.map(book => book.favorite),[true,false,false]);
    assert.equal((await page.locator('#batchFavoriteButton').textContent()).trim(),'批量收藏');
    fixture.failFavorite = false;
    await page.locator('#batchFavoriteButton').click(); await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled);
    assert.ok(fixture.books.every(book => book.favorite));
    fixture.failFavorite = true;
    await page.locator('#batchFavoriteButton').click(); await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled);
    assert.deepEqual(fixture.books.map(book => book.favorite),[true,false,true]);
    assert.equal((await page.locator('#batchFavoriteButton').textContent()).trim(),'撤销操作');
    fixture.failFavorite = false;
    await page.locator('#batchFavoriteButton').click(); await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled);
    assert.deepEqual(fixture.favoriteRequests.at(-1),['103']);
    assert.deepEqual(fixture.books.map(book => book.favorite),[true,false,false]);
    await page.locator('#batchFavoriteButton').click(); await page.waitForFunction(() => !document.querySelector('#batchFavoriteButton').disabled);
    assert.ok(fixture.books.every(book => book.favorite));
    await manage('selectedList');
    await row('selectedList','101').locator('.bulk-choice').click();
    assert.equal(await row('selectedList','101').locator('.bulk-choice').isChecked(),true);
    await row('selectedList','101').locator('.bulk-choice').press('Space');
    assert.equal(await row('selectedList','101').locator('.bulk-choice').isChecked(),false);
    await row('selectedList','101').locator('.bulk-choice').press('Space');
    await toolbar('selectedList').locator('[data-bulk-delete]').click();
    await page.locator('.bulk-dialog button[value="cancel"]').click();
    assert.equal(await page.locator('#selectedList .selected-item').count(),3);
    await toolbar('selectedList').locator('[data-bulk-delete]').click(); await confirm();
    await count('#selectedList .selected-item',2); await closeOverlay();
    await nav('shelf'); await count('#shelfList .shelf-item',3);
    await manage('shelfList'); await row('shelfList','101').locator('.bulk-choice').click();
    await page.evaluate(() => document.querySelectorAll('.toast').forEach(node => node.remove()));
    assert.equal(await row('shelfList','101').evaluate(node => getComputedStyle(node).outlineStyle),'none');
    await page.screenshot({ path:resolve(output, client + '-bulk.png') });
    await toolbar('shelfList').locator('[data-bulk-delete]').click(); await confirm();
    await count('#shelfList .shelf-item',2);
    assert.ok(fixture.books[0].favorite && fixture.books[0].local); assert.equal(fixture.bookmarks.length,2);
    await page.locator('[data-shelf-filter="bookmarks"]').click(); await count('#shelfList .shelf-item',2);
    await manage('shelfList'); await row('shelfList','101|1011|0').locator('.bulk-choice').click();
    await toolbar('shelfList').locator('[data-bulk-delete]').click(); await confirm(); await count('#shelfList .shelf-item',1);
    assert.equal(fixture.bookmarks[0].page,1);
    await page.locator('[data-shelf-filter="local"]').click(); await count('#shelfList .shelf-item',1);
    await manage('shelfList'); await toolbar('shelfList').locator('[data-bulk-all]').click();
    await toolbar('shelfList').locator('[data-bulk-delete]').click(); await confirm(); await count('#shelfList .shelf-item',0);
    assert.equal(fixture.books[0].favorite,true);
    await page.locator('[data-shelf-filter="favorite"]').click(); await count('#shelfList .shelf-item',3);
    await manage('shelfList'); await row('shelfList','101').locator('.bulk-choice').click();
    await toolbar('shelfList').locator('[data-bulk-delete]').click(); await confirm(); await count('#shelfList .shelf-item',2);
    assert.equal(fixture.bookmarks.length,1);
    await nav('history'); await count('#historyList .history-item',3);
    await manage('historyList'); await row('historyList','101').locator('.bulk-choice').click();
    await toolbar('historyList').locator('[data-bulk-delete]').click(); await confirm(); await count('#historyList .history-item',2);
    await page.locator('[data-history-filter="failed"]').click();
    assert.equal(await toolbar('historyList').locator('[data-bulk-manage]').isVisible(),true);
    await page.locator('[data-history-filter="all"]').click(); await page.reload(); await count('.album-card',3);
    await nav('history'); await count('#historyList .history-item',2);
    assert.ok(!(await page.evaluate(() => JSON.parse(localStorage.getItem('jm-history-v2')))).some(item => item.id === '101'));
    await nav('queue'); await count('#taskList .task-item',3);
    assert.equal(await page.locator('#taskList [data-remove-task]').count(),2);
    assert.equal(await page.locator('#taskList [data-task="103"] [data-remove-task]').count(),0);
    if (client === 'mobile') {
      await page.locator('[data-stat-filter="failed"]').click(); await count('#taskList .task-item',1);
      await manage('taskList'); await toolbar('taskList').locator('[data-bulk-all]').click();
      assert.equal(await row('taskList','102').locator('.bulk-choice').isChecked(),true);
      assert.equal(await row('taskList','101').count(),0);
      await toolbar('taskList').locator('[data-bulk-delete]').click(); await confirm();
      await count('#taskList .task-item',0); assert.deepEqual(fixture.removedTasks,['102']);
      await page.locator('[data-stat-filter="all"]').click(); await count('#taskList .task-item',2);
    } else {
      await page.locator('[data-remove-task="102"]').click(); await confirm();
      await count('#taskList .task-item',2); assert.deepEqual(fixture.removedTasks,['102']);
    }
    await manage('taskList');
    assert.equal(await page.locator('#taskList .bulk-choice').count(),1);
    await toolbar('taskList').locator('[data-bulk-all]').click(); await toolbar('taskList').locator('[data-bulk-delete]').click(); await confirm();
    await count('#taskList .task-item',1); assert.deepEqual(fixture.removedTasks,['101']);
    fixture.snapshot.tasks.push({ item_id:'102', status:'failed', base_dir:'Fixture folder' });
    if (client === 'mobile') await page.locator('#queueRefresh').click(); else { await page.reload(); await nav('queue'); }
    await count('#taskList .task-item',2);
    await page.locator('[data-remove-task="102"]').click(); await confirm(); await count('#taskList .task-item',1);
    if (client === 'mobile') await page.locator('[data-queue-panel="logs"]').click();
    await manage('logList'); await toolbar('logList').locator('[data-bulk-all]').click();
    await toolbar('logList').locator('[data-bulk-delete]').click(); await confirm(); await count('#logList .log-entry',0);
    await nav('discover'); if (client === 'mobile') await page.locator('#searchEntry').click();
    await manage('recentSearches'); await toolbar('recentSearches').locator('[data-bulk-all]').click();
    await toolbar('recentSearches').locator('[data-bulk-delete]').click(); await confirm(); await count('#recentSearches .recent-entry',0);
    if (client === 'mobile') await page.locator('#searchCancel').click();
    fixture.snapshot = { ...fixture.snapshot, run_id:'fixture-next-run', running:false, tasks:[{ item_id:'101', status:'success', base_dir:'Fixture folder' }], last_success_ids:['101'], last_failed_ids:[] };
    await nav('queue');
    if (client === 'mobile') await page.locator('#queueRefresh').click();
    else await page.reload();
    await nav('history'); await count('#historyList .history-item',3);
    assert.ok((await page.evaluate(() => JSON.parse(localStorage.getItem('jm-history-v2')))).some(item => item.id === '101'));
    assert.deepEqual(errors,[],client + ' script errors');
    results.push({ client, passed:true, checks:['detail favorite','single favorite state', 'batch delta-only undo and partial undo retry','selection and cancel','reading history','exact bookmark deletion','local shelf','favorites','download history persistence','filter selection reset','direct failed-task deletion', 'filtered failed-task deletion', 'completed tasks only','activity logs','search history','new download history restoration'] });
    await context.close();
  }
  console.log(JSON.stringify({ passed:true, results }, null, 2));
} finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
