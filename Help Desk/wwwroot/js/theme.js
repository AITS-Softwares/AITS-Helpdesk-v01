(() => {
    const button = document.getElementById('theme-toggle');
    if (!button) return;
    const root = document.documentElement;
    const label = button.querySelector('.theme-toggle-label');
    const icon = button.querySelector('[aria-hidden="true"]');
    const update = () => {
        const dark = root.dataset.theme === 'dark';
        button.setAttribute('aria-pressed', String(dark));
        button.setAttribute('aria-label', dark ? 'Switch to light theme' : 'Switch to dark theme');
        label.textContent = dark ? 'Light mode' : 'Dark mode';
        icon.textContent = dark ? '☀' : '☾';
        document.querySelector('meta[name="theme-color"]')?.setAttribute('content', dark ? '#111827' : '#171e32');
    };
    update();
    button.addEventListener('click', () => {
        if (root.dataset.theme === 'dark') delete root.dataset.theme;
        else root.dataset.theme = 'dark';
        try { localStorage.setItem('helpdesk-theme', root.dataset.theme || 'light'); } catch { }
        update();
    });
})();
