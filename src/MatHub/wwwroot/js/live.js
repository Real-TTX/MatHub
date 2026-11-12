/* MatHub – pages that show something that is still running (jobs, runs) load themselves again every few seconds: an element with
   data-live-target="name" and data-live-refresh="5" asks for it. The page is fetched and only the element is replaced (no jump, the focus and the scroll position stay); when nothing
   runs any more the new element has no data-live-refresh and the refreshing stops. Without a network it waits (offline.js shows that). */
(function () {
  'use strict';
  var live = document.querySelector('[data-live-refresh]');
  if (!live) { return; }

  var seconds = Math.max(2, parseInt(live.getAttribute('data-live-refresh'), 10) || 5);
  function tick() {
    if (document.hidden || !navigator.onLine) { setTimeout(tick, seconds * 1000); return; }
    fetch(location.href, { headers: { Accept: 'text/html' }, credentials: 'same-origin', cache: 'no-store' })
      .then(function (r) { return r.ok ? r.text() : null; })
      .then(function (html) {
        if (!html) { setTimeout(tick, seconds * 1000); return; }
        var doc = new DOMParser().parseFromString(html, 'text/html');
        var fresh = doc.querySelector('[data-live-target="' + live.getAttribute('data-live-target') + '"]');
        var current = document.querySelector('[data-live-target="' + live.getAttribute('data-live-target') + '"]');
        if (!fresh || !current) { return; }
        current.replaceWith(document.importNode(fresh, true));
        if (window.MatUi && window.MatUi.refresh) { window.MatUi.refresh(document.querySelector('main')); }
        live = document.querySelector('[data-live-refresh]');
        if (live) { setTimeout(tick, seconds * 1000); }
      })
      .catch(function () { setTimeout(tick, seconds * 1000); });
  }

  setTimeout(tick, seconds * 1000);
})();
