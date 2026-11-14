/* MatHub as an app (PWA): the service worker, the install button and the clean-up when somebody signs out.
   - The service worker (sw.js) keeps the shell of the app, the last folders for offline use.
   - "Install the app": the browser offers it by an event; the button (data-pwa-install, in the user menu) is shown only when the browser can do it.
   - Signing out clears what the service worker and the editor kept on this device (copies of folders and changes that wait belong to the person who was signed in). */
(function () {
  'use strict';

  if ('serviceWorker' in navigator && window.isSecureContext) {
    window.addEventListener('load', function () { navigator.serviceWorker.register('/sw.js').catch(function () { /* the app works without it */ }); });
  }

  var deferred = null;
  window.addEventListener('beforeinstallprompt', function (e) {
    e.preventDefault();
    deferred = e;
    Array.prototype.forEach.call(document.querySelectorAll('[data-pwa-install]'), function (b) { b.hidden = false; });
  });
  window.addEventListener('appinstalled', function () {
    deferred = null;
    Array.prototype.forEach.call(document.querySelectorAll('[data-pwa-install]'), function (b) { b.hidden = true; });
  });
  document.addEventListener('click', function (e) {
    var button = e.target.closest && e.target.closest('[data-pwa-install]');
    if (!button || !deferred) { return; }
    deferred.prompt();
    deferred.userChoice.finally(function () { deferred = null; button.hidden = true; });
  });

  document.addEventListener('submit', function (e) {
    var form = e.target;
    if (!form || !form.matches || !form.matches('[data-logout]')) { return; }
    try {
      Object.keys(localStorage).filter(function (k) { return k.indexOf('mh.pending.') === 0 || k.indexOf('mh.hidden.') === 0; }).forEach(function (k) { localStorage.removeItem(k); });
    } catch (err) { /* private mode */ }
    if (window.caches) { caches.delete('mh-pages'); }
    if (navigator.serviceWorker && navigator.serviceWorker.controller) { navigator.serviceWorker.controller.postMessage('mh-clear'); }
  });

  // the menu shows where one is: the current entry scrolled into the visible part of a long menu (the rollout pages are far down)
  var current = document.querySelector('.mat-nav__scroll:not([hidden]) [aria-current="page"]');
  if (current && current.scrollIntoView) { current.scrollIntoView({ block: 'nearest' }); }
  // the tabs of a record on a phone scroll sideways: the open one is in view (and the row shows it goes on)
  var tab = document.querySelector('.mat-tabs .mat-tab.is-active');
  var tabs = tab && tab.parentElement;
  if (tabs && tabs.scrollWidth > tabs.clientWidth) {
    tabs.scrollLeft = Math.max(0, tab.offsetLeft - (tabs.clientWidth - tab.offsetWidth) / 2);
    tabs.classList.add('mh-tabs-scroll');
  }
})();
