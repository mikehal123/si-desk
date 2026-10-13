/* Shell cache only. Never cache /api/. No push subscription. No background sync. */
var CACHE = "si-desk-phone-v3";
/* index.html is served at /. Cache that response under /index.html. */
var SHELL = [
  { path: "/", key: "/index.html" },
  { path: "/app.css", key: "/app.css" },
  { path: "/app.js", key: "/app.js" },
  { path: "/manifest.webmanifest", key: "/manifest.webmanifest" },
  { path: "/icon-192.png", key: "/icon-192.png" },
  { path: "/icon-512.png", key: "/icon-512.png" }
];
var ASSETS = SHELL.map(function (item) { return item.key; });

self.addEventListener("install", function (event) {
  event.waitUntil(
    caches.open(CACHE).then(function (cache) {
      return Promise.all(SHELL.map(function (item) {
        return fetch(item.path, { cache: "reload" }).then(function (response) {
          if (response && response.ok && response.type === "basic") return cache.put(item.key, response);
        }).catch(function () {
          /* One missing icon must not discard the rest of the shell. */
        });
      }));
    }).then(function () {
      return self.skipWaiting();
    })
  );
});

self.addEventListener("activate", function (event) {
  event.waitUntil(
    caches.keys().then(function (keys) {
      return Promise.all(keys.filter(function (key) {
        return key !== CACHE;
      }).map(function (key) {
        return caches.delete(key);
      }));
    }).then(function () {
      return self.clients.claim();
    })
  );
});

self.addEventListener("fetch", function (event) {
  var request = event.request;
  if (request.method !== "GET") return;
  if (request.cache === "only-if-cached" && request.mode !== "same-origin") return;
  var url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  if (url.pathname.indexOf("/api/") === 0) return;

  var key = null;
  if (ASSETS.indexOf(url.pathname) !== -1) key = url.pathname;
  else if (request.mode === "navigate" && (url.pathname === "/" || url.pathname === "/index.html")) key = "/index.html";
  if (!key) return;

  event.respondWith(networkThenCache(request, key));
});

function networkThenCache(request, key) {
  return fetch(request).then(function (response) {
    if (response && response.ok && response.type === "basic") {
      var copy = response.clone();
      caches.open(CACHE).then(function (cache) {
        return cache.put(key, copy);
      });
    }
    return response;
  }).catch(function () {
    return caches.match(key).then(function (cached) {
      if (cached) return cached;
      return new Response("SI Desk is offline.", {
        status: 503,
        headers: { "Content-Type": "text/plain; charset=utf-8" }
      });
    });
  });
}

self.addEventListener("message", function (event) {
  var data = event.data;
  if (!data || data.type !== "notify") return;
  var title = typeof data.title === "string" && data.title ? data.title : "SI Desk";
  var body = typeof data.body === "string" ? data.body : "";
  event.waitUntil(
    self.registration.showNotification(title, {
      body: body,
      icon: "/icon-192.png"
    }).catch(function () {})
  );
});

self.addEventListener("notificationclick", function (event) {
  event.notification.close();
  event.waitUntil(
    self.clients.matchAll({ type: "window", includeUncontrolled: true }).then(function (clients) {
      var i;
      for (i = 0; i < clients.length; i++) {
        if (clients[i].focus) return clients[i].focus();
      }
      if (self.clients.openWindow) return self.clients.openWindow("/");
    })
  );
});
