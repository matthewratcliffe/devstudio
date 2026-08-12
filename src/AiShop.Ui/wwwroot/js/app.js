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

if ('serviceWorker' in navigator) {
    window.addEventListener('load', () => navigator.serviceWorker.register('/service-worker.js').catch(() => { }));
}
