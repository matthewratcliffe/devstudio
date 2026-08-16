// Small helpers the Blazor components call into. Everything else is server-rendered.
window.aiShop = {
    // Keeps a transcript pinned to the newest message unless the user has scrolled up to read.
    scrollToBottom: function (element, force) {
        if (!element) return;
        const distanceFromBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
        if (force || distanceFromBottom < 160) {
            element.scrollTop = element.scrollHeight;
        }
    },
    // Stops Enter from inserting a newline so it can send instead. Blazor still gets the keydown
    // and decides what to do with it; only the browser's default is cancelled here, because
    // Blazor fixes preventDefault at render time and cannot make the Shift+Enter distinction.
    sendOnEnter: function (element) {
        if (!element || element.dataset.sendOnEnter) return;
        element.dataset.sendOnEnter = '1';
        element.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
                e.preventDefault();
            }
        });
    },
    focus: function (element) {
        if (element) element.focus();
    },
    copy: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },
    openWindow: function (url) {
        window.open(url, '_blank', 'noopener');
    }
};

// Timestamps are rendered as <time datetime="...utc..." data-fmt="..."> and formatted here, in the
// reader's own timezone. The server cannot do this: it runs in a container set to UTC, so its idea
// of local time is nobody's.
(function () {
    const shapes = {
        time: { hour: '2-digit', minute: '2-digit', second: '2-digit' },
        short: { hour: '2-digit', minute: '2-digit' },
        daytime: { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' },
        weekday: { weekday: 'short', hour: '2-digit', minute: '2-digit' },
        weekdaydate: { weekday: 'short', day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' },
        date: { day: '2-digit', month: 'short' },
        full: { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' }
    };

    const formatters = {};

    function formatterFor(name) {
        if (!formatters[name]) {
            const shape = shapes[name] || shapes.time;
            formatters[name] = new Intl.DateTimeFormat(navigator.language || 'en-GB', { ...shape, hour12: false });
        }
        return formatters[name];
    }

    function format(element) {
        const value = element.getAttribute('datetime');
        if (!value || element.dataset.localised === value) return;

        const when = new Date(value);
        if (isNaN(when)) return;

        element.textContent = formatterFor(element.dataset.fmt).format(when).replace(/,/g, '');
        // Keyed by the value, so a re-render with a new instant is formatted again.
        element.dataset.localised = value;
    }

    function formatAll(root) {
        if (root.nodeType !== 1) return;
        if (root.matches && root.matches('time[data-fmt]')) format(root);
        (root.querySelectorAll ? root.querySelectorAll('time[data-fmt]') : []).forEach(format);
    }

    // Blazor replaces nodes as the transcript streams, so this watches rather than running once.
    const observer = new MutationObserver(function (records) {
        for (const record of records) {
            record.addedNodes.forEach(formatAll);
            if (record.type === 'attributes') format(record.target);
        }
    });

    function start() {
        formatAll(document.body);
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributeFilter: ['datetime']
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();

if ('serviceWorker' in navigator) {
    window.addEventListener('load', () => navigator.serviceWorker.register('/service-worker.js').catch(() => { }));
}

// Offers to install the PWA when the app is loaded as a plain browser tab. Skipped inside the
// desktop shell (already a native window) and inside the installed PWA itself (already running
// standalone), so the prompt only ever shows to someone who could actually benefit from it.
(function () {
    const DISMISS_KEY = 'devstudio:install-dismissed-at';
    const DISMISS_DAYS = 1;

    const isDesktopShell = !!(window.chrome && window.chrome.webview);
    const isStandalone = window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
    if (isDesktopShell || isStandalone) return;

    let deferredPrompt = null;

    function recentlyDismissed() {
        const at = Number(localStorage.getItem(DISMISS_KEY) || 0);
        return at > 0 && (Date.now() - at) < DISMISS_DAYS * 24 * 60 * 60 * 1000;
    }

    function showBanner() {
        if (document.querySelector('.pwa-install-banner') || recentlyDismissed()) return;

        const banner = document.createElement('div');
        banner.className = 'pwa-install-banner';
        banner.setAttribute('role', 'complementary');
        banner.innerHTML =
            '<span>Install devStudio for a faster, full-screen experience.</span>' +
            '<span class="pwa-install-actions">' +
            '<button type="button" class="btn btn-sm btn-primary" data-action="install">Install</button>' +
            '<button type="button" class="btn btn-sm btn-ghost" data-action="dismiss">Not now</button>' +
            '</span>';

        banner.querySelector('[data-action="install"]').addEventListener('click', async () => {
            banner.remove();
            if (!deferredPrompt) return;
            deferredPrompt.prompt();
            await deferredPrompt.userChoice;
            deferredPrompt = null;
        });

        banner.querySelector('[data-action="dismiss"]').addEventListener('click', () => {
            localStorage.setItem(DISMISS_KEY, String(Date.now()));
            banner.remove();
        });

        document.body.appendChild(banner);
    }

    window.addEventListener('beforeinstallprompt', event => {
        event.preventDefault();
        deferredPrompt = event;
        showBanner();
    });

    window.addEventListener('appinstalled', () => {
        deferredPrompt = null;
        localStorage.removeItem(DISMISS_KEY);
        const banner = document.querySelector('.pwa-install-banner');
        if (banner) banner.remove();
    });
})();

// The desktop shells show this page in a window with no address bar, no reload button and no
// context menu, and their caches outlive the build that filled them. Ctrl+Shift+R is the way back:
// the same shortcut a browser uses, doing as much of the same thing as a page can do for itself.
// The HTTP cache is the shell's to clear, so on Windows the shell is asked and it reloads; anywhere
// else this is everything available.
window.addEventListener('keydown', async function (e) {
    if (!e.ctrlKey || !e.shiftKey || e.key.toLowerCase() !== 'r') return;

    e.preventDefault();

    try {
        if ('serviceWorker' in navigator) {
            const workers = await navigator.serviceWorker.getRegistrations();
            await Promise.all(workers.map(worker => worker.unregister()));
        }

        if (window.caches) {
            const names = await caches.keys();
            await Promise.all(names.map(name => caches.delete(name)));
        }
    } catch {
        // Whatever could not be cleared, reloading is still worth doing.
    }

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage('devstudio:hard-reload');
    } else {
        location.reload();
    }
});
