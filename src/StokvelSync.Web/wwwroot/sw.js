const CACHE='stokvelsync-shell-v1';
const SHELL=['/','/demo','/css/app.css','/js/app.js','/js/data.js','/js/i18n.js','/icon.svg','/manifest.webmanifest','/fonts/dm-sans-latin-400-normal.woff2','/fonts/dm-sans-latin-500-normal.woff2','/fonts/dm-sans-latin-600-normal.woff2','/fonts/manrope-latin-700-normal.woff2'];
self.addEventListener('install',event=>event.waitUntil(caches.open(CACHE).then(async cache=>{await cache.addAll(SHELL);await self.skipWaiting();})));
self.addEventListener('activate',event=>event.waitUntil(caches.keys().then(keys=>Promise.all(keys.filter(k=>k.startsWith('stokvelsync-shell-')&&k!==CACHE).map(k=>caches.delete(k)))).then(()=>self.clients.claim())));
self.addEventListener('fetch',event=>{
 const url=new URL(event.request.url);if(url.origin!==location.origin||event.request.method!=='GET'||url.pathname.startsWith('/api/'))return;
 if(event.request.mode==='navigate'){event.respondWith(fetch(event.request).catch(async()=>{const cache=await caches.open(CACHE);return (await cache.match(url.pathname==='/demo'?'/demo':'/'))||Response.error();}));return;}
 event.respondWith(caches.match(event.request).then(cached=>cached||fetch(event.request)));
});
