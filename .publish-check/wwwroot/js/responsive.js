(() => {
    const sidebar = document.querySelector('.sidebar');
    const toggle = document.getElementById('navigation-toggle');
    if (sidebar && toggle) {
        sidebar.classList.add('navigation-ready');
        const close = () => { sidebar.removeAttribute('data-menu-open'); toggle.setAttribute('aria-expanded', 'false'); };
        toggle.addEventListener('click', () => {
            const open = !sidebar.hasAttribute('data-menu-open');
            sidebar.toggleAttribute('data-menu-open', open);
            toggle.setAttribute('aria-expanded', String(open));
        });
        sidebar.addEventListener('keydown', event => {
            if (event.key === 'Escape') { close(); toggle.focus(); }
        });
        sidebar.querySelectorAll('nav a').forEach(link => link.addEventListener('click', close));
        window.matchMedia('(min-width: 721px)').addEventListener('change', close);
    }
    document.querySelectorAll('.table-scroll').forEach(region => {
        const table = region.querySelector('table');
        if (!table) return;
        region.tabIndex = 0;
        region.setAttribute('role', 'region');
        region.setAttribute('aria-label', 'Data table');
        region.classList.add('cards-ready');
        const headings = Array.from(table.querySelectorAll('thead th')).map(th => th.textContent.trim());
        table.querySelectorAll('tbody tr').forEach(row => {
            Array.from(row.children).forEach((cell, index) => {
                if (cell.tagName === 'TD') cell.dataset.label = headings[index] || 'Actions';
            });
        });
    });
})();
