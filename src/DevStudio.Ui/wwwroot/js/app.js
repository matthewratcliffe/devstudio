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
