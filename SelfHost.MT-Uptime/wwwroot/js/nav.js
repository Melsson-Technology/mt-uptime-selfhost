// Opens and closes the collapsed navigation on narrow viewports.
//
// This lives in a file rather than in the layout for two reasons. The first is the same one that moved
// the copy buttons out of their onclick attributes: `script-src 'self'` is the directive worth keeping,
// and any inline script forfeits it. The second is that MainLayout is statically rendered — no page sets
// a render mode on it — so it has no interactive circuit and therefore no @onclick to flip a field with.
//
// One delegated listener on the document rather than wiring the button directly: Blazor's enhanced
// navigation patches the DOM between pages, and a listener bound to an element it later replaces stops
// working silently. Everything below re-reads the DOM on each event for the same reason.
//
// The open state is an attribute on the topbar (`data-nav-open`), which is what app.css keys off.

document.addEventListener('click', (event) => {
    const target = event.target;
    if (!(target instanceof Element)) return;

    const bar = document.querySelector('.topbar');
    if (!bar) return; // The public status page uses a chrome-free layout and has no topbar.

    if (target.closest('[data-nav-toggle]')) {
        setNavOpen(bar, !bar.hasAttribute('data-nav-open'));
        return;
    }

    // Every other click closes it. That covers the two cases separately: tapping a link inside the panel
    // (enhanced navigation patches the page underneath, so the panel would otherwise stay open over the
    // page it just navigated to), and tapping anywhere outside it.
    if (bar.hasAttribute('data-nav-open')) setNavOpen(bar, false);
});

document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') return;
    const bar = document.querySelector('.topbar');
    if (bar && bar.hasAttribute('data-nav-open')) setNavOpen(bar, false);
});

function setNavOpen(bar, open) {
    if (open) bar.setAttribute('data-nav-open', '');
    else bar.removeAttribute('data-nav-open');

    // aria-expanded is the part a screen reader reads; keep it with the attribute the CSS uses rather
    // than letting the two drift.
    const toggle = bar.querySelector('[data-nav-toggle]');
    if (toggle) toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
}
