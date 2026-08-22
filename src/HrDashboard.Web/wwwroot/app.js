window.hrDashboard = {
    scrollToBottom: function (element) {
        if (element) element.scrollTop = element.scrollHeight;
    },
    // Stops the textarea's default newline insertion for plain Enter so Blazor's
    // Immediate-mode value sync (triggered by that same keystroke's input event) can't
    // race the server-side clear that happens after submitting. Shift+Enter is untouched,
    // so its native newline insertion still works.
    preventEnterSubmit: function (elementId) {
        var el = document.getElementById(elementId);
        if (!el || el.dataset.hrEnterSubmitBound) return;
        el.dataset.hrEnterSubmitBound = "true";
        el.addEventListener("keydown", function (e) {
            if (e.key === "Enter" && !e.shiftKey) e.preventDefault();
        });
    }
};
