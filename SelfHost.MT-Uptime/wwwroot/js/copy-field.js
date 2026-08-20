// Click-to-select and copy-to-clipboard for the read-only credential fields (the push monitor's ping
// URL, on both the monitor editor and the monitor detail page).
//
// These behaviours used to live in `onclick="…"` attributes on the elements themselves. That works, but
// an inline handler is script in the document, so it can only run under a Content-Security-Policy that
// allows 'unsafe-inline' for scripts — which is the single directive worth having, since it is what
// turns an HTML injection anywhere in the app into script execution. Four attributes were the only
// thing standing between this app and `script-src 'self'`, so they moved here.
//
// One delegated listener on the document rather than per-element wiring: the fields are rendered inside
// Blazor components that re-render on their own schedule, and a listener attached to an element that
// Blazor later replaces would silently stop working.
document.addEventListener('click', (event) => {
    const target = event.target;
    if (!(target instanceof Element)) return;

    const selectable = target.closest('[data-select-on-click]');
    if (selectable instanceof HTMLInputElement) {
        selectable.select();
        return;
    }

    const button = target.closest('[data-copy-previous]');
    if (!(button instanceof HTMLElement)) return;

    const source = button.previousElementSibling;
    if (!(source instanceof HTMLInputElement)) return;

    // navigator.clipboard needs a secure context (HTTPS, or localhost). That was equally true of the
    // inline version this replaces, so it is not a regression — but say so rather than failing silently,
    // because the value on screen is still selectable and copyable by hand.
    if (!navigator.clipboard) {
        source.select();
        button.textContent = 'Press Ctrl+C';
        return;
    }

    navigator.clipboard.writeText(source.value).then(
        () => { button.textContent = 'Copied!'; },
        () => { source.select(); button.textContent = 'Press Ctrl+C'; });
});
