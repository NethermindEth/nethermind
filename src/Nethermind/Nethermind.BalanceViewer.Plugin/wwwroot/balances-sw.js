// Service worker for the balance viewer: lets account-activity notifications show as
// system notifications (including for the page installed to a mobile home screen)
// and focuses the viewer when one is clicked. All data still comes from the node —
// there is no push subscription to any third-party service.
'use strict';

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
        const existing = windows.find((w) => w.url.includes('/balances'));
        return existing ? existing.focus() : self.clients.openWindow('/balances');
    }));
});
