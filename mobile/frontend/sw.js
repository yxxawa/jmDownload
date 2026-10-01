/* JM Download 手机端 Service Worker
   只缓存界面资源；/api、/ws 与图片请求一律直连，避免拿到过期的下载状态。 */
const CACHE = 'jm-mobile-shell-v1';
const SHELL = [
  './',
  './index.html',
  './styles.css',
  './reader.css',
  './app.js',
  './reader.js',
  './manifest.webmanifest',
  'icons/icon.svg',
  'icons/icon-192.png',
  'icons/icon-512.png'
];

self.addEventListener('install', event => {
  event.waitUntil((async () => {
    const cache = await caches.open(CACHE);
    await Promise.allSettled(SHELL.map(url => cache.add(new Request(url, { cache: 'reload' }))));
    await self.skipWaiting();
  })());
});

self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    const names = await caches.keys();
    await Promise.all(names.filter(name => name !== CACHE).map(name => caches.delete(name)));
    await self.clients.claim();
  })());
});

const isApi = url =>
  url.pathname.startsWith('/api/') ||
  url.pathname.startsWith('/ws/') ||
  url.pathname === '/health' ||
  url.pathname.startsWith('/images/');

self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.method !== 'GET') return;

  const url = new URL(request.url);
  if (url.origin !== location.origin || isApi(url)) return;

  if (request.mode === 'navigate') {
    event.respondWith((async () => {
      try {
        return await fetch(request);
      } catch {
        const cache = await caches.open(CACHE);
        return (await cache.match('./index.html')) || Response.error();
      }
    })());
    return;
  }

  event.respondWith((async () => {
    const cache = await caches.open(CACHE);
    const cached = await cache.match(request);
    if (cached) {
      fetch(request).then(response => { if (response.ok) cache.put(request, response.clone()); }).catch(() => { });
      return cached;
    }
    try {
      const response = await fetch(request);
      if (response.ok && response.type === 'basic') cache.put(request, response.clone());
      return response;
    } catch {
      return Response.error();
    }
  })());
});
