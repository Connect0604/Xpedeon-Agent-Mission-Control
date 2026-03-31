// Xpedeon Agent Mission Control — client-side helpers

window.AMC = {
    // Scroll a container to bottom (used by log feed)
    scrollToBottom: function (elementId) {
        const el = document.getElementById(elementId);
        if (el) el.scrollTop = el.scrollHeight;
    },

    // Scroll to top
    scrollToTop: function (elementId) {
        const el = document.getElementById(elementId);
        if (el) el.scrollTop = 0;
    },

    // Copy text to clipboard
    copyToClipboard: function (text) {
        if (navigator.clipboard) {
            navigator.clipboard.writeText(text);
        }
    }
};
