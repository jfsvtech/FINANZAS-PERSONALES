// Service worker minimo: requisito para que el navegador ofrezca "instalar" la app.
// No cachea datos: la informacion financiera siempre se lee fresca del servidor.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(clients.claim()));
self.addEventListener('fetch', () => { /* red directa */ });
