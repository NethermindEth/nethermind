// Service worker: shows account-activity notifications and focuses the viewer when one is clicked.
'use strict';

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
        const existing = windows.find((w) => w.url.includes('/portfolio'));
        return existing ? existing.focus() : self.clients.openWindow('/portfolio');
    }));
});
