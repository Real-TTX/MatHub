/* MatHub service worker: the app as a PWA – small on purpose.
   - Static files (toolkit, styles, scripts, icons): from the cache first, refreshed in the background.
   - The pages people look at on the way (the overview, the clients, a client, the jobs, the updates) come from the network; a copy of the last ones is kept, so
     without a network their last state is still shown (read-only in effect: every change needs the network; offline.js says so).
     Other pages without a network: the offline page (offline.html) with a button to try again.
   - The APIs, the agents, the network boot and every change (POST, PUT, DELETE) are never touched. */
const VERSION = 'mathub-v1';
const PAGES = 'mh-pages';
const SHELL = [
  '/offline.html', '/js/offline.js', '/js/pwa.js', '/js/util.js', '/matui/matui.css', '/matui/matui.js', '/matui/matui.boot.js', '/css/app.css', '/css/hub.css', '/favicon.svg',
  '/img/icon-192.png', '/img/icon-512.png', '/img/logo/mathub-logo.svg', '/img/logo/mathub-logo-dark.svg', '/manifest.webmanifest',
];
const MAX_PAGES = 40;

self.addEventListener('install', event => {
  event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== VERSION && k !== PAGES).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

// signing out clears what was kept for this person (sent by pwa.js)
self.addEventListener('message', event => {
  if (event.data === 'mh-clear') {
    event.waitUntil(caches.delete(PAGES));
  }
});

function isStatic(url) {
  return /^\/(matui|css|js|img)\//.test(url.pathname) || url.pathname === '/favicon.svg' || url.pathname === '/manifest.webmanifest' || url.pathname === '/offline.html';
}

/** The pages worth keeping for a look without a network. */
function isKeptPage(url) {
  return url.pathname === '/' || /^\/(clients|jobs|updates|groups|packages|scripts)$/.test(url.pathname)
    || /^\/clients\/\d+(\/(inventory|software|jobs|activity))?$/.test(url.pathname) || /^\/jobs\/\d+$/.test(url.pathname);
}

function trim(cache, max) {
  return cache.keys().then(keys => (keys.length > max ? cache.delete(keys[0]) : undefined));
}

function networkFirst(request, cacheName, max, fallback) {
  return fetch(request).then(response => {
    if (response.ok) {
      const copy = response.clone();
      caches.open(cacheName).then(cache => cache.put(request, copy).then(() => trim(cache, max)));
    }
    return response;
  }).catch(() => caches.open(cacheName).then(cache => cache.match(request)).then(hit => hit || (fallback ? caches.match(fallback) : Response.error())));
}

self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname === '/sw.js' || url.pathname === '/health') return;
  if (/^\/(api|live|boot|agent)\//.test(url.pathname)) return;

  if (request.mode === 'navigate') {
    event.respondWith(isKeptPage(url) ? networkFirst(request, PAGES, MAX_PAGES, '/offline.html') : fetch(request).catch(() => caches.match('/offline.html')));
    return;
  }

  // scripts and styles without a version in the address: from the network while there is one, so a new build is used at once
  if (isStatic(url) && !url.search.includes('v=') && /^\/(js|css)\//.test(url.pathname)) { event.respondWith(networkFirst(request, VERSION, 500)); return; }

  if (isStatic(url)) {
    event.respondWith(caches.open(VERSION).then(cache => cache.match(request).then(hit => {
      const fresh = fetch(request).then(response => { if (response.ok) cache.put(request, response.clone()); return response; }).catch(() => hit);
      return hit || fresh;
    })));
  }
});
