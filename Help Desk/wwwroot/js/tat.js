(() => {
    const panel = document.querySelector('[data-tat-alerts]');
    const cells = [...document.querySelectorAll('[data-tat-cell]')].map(el => ({
        el, due: Date.parse(el.dataset.due), start: Date.parse(el.dataset.now), tick: performance.now()
    }));
    function updateClocks() {
        for (const { el, due, start, tick } of cells) {
            if (el.dataset.complete === 'true') continue;
            const now = start + performance.now() - tick;
            const late = now >= due;
            const minutes = Math.floor(Math.abs(due - now) / 60000);
            el.querySelector('[data-tat-countdown]').textContent = `${Math.floor(minutes / 1440)}d ${Math.floor(minutes / 60) % 24}h ${minutes % 60}m ${late ? 'overdue' : 'left'}`;
            const label = el.querySelector('[data-tat-label]');
            label.textContent = late ? 'Overdue' : 'Within TAT';
            label.classList.toggle('tat-overdue', late);
            label.classList.toggle('status-active', !late);
        }
    }
    let pending = false;
    let previous = panel ? [...panel.querySelectorAll('[data-tat-alert-items] a')].map(a => a.getAttribute('href')).join(',') : '';
    async function checkAlerts() {
        if (!panel || pending || document.hidden) return;
        pending = true;
        try {
            const response = await fetch(panel.dataset.endpoint, { cache: 'no-store', headers: { Accept: 'application/json' } });
            if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) return;
            const result = await response.json();
            panel.hidden = result.count === 0;
            panel.querySelector('[data-tat-alert-title]').textContent = `${result.count} ticket(s) missed the TAT deadline`;
            const container = panel.querySelector('[data-tat-alert-items]');
            const fragment = document.createDocumentFragment();
            for (const ticket of result.tickets) {
                const link = document.createElement('a');
                link.href = panel.dataset.endpoint.replace(/\/TatAlerts(?:\?.*)?$/, `/Details/${ticket.id}`);
                const id = document.createElement('strong');
                id.textContent = `#${String(ticket.id).padStart(4, '0')}`;
                const title = document.createElement('span');
                title.textContent = ticket.title;
                const action = document.createElement('small');
                action.textContent = 'View ticket →';
                link.append(id, title, action);
                fragment.append(link);
            }
            container.replaceChildren(fragment);
            panel.querySelector('.tat-alert-footer').textContent = `Showing the earliest ${result.tickets.length} overdue tickets · Checks every 30 seconds while this page is open.`;
            const current = [...container.querySelectorAll('a')].map(a => a.getAttribute('href')).join(',');
            if (current !== previous) document.querySelector('[data-tat-announcement]').textContent = result.count ? `${result.count} overdue tickets need attention.` : 'No overdue tickets need attention.';
            previous = current;
        } catch { /* Retain the most recent server-rendered alerts during network interruptions. */ }
        finally { pending = false; }
    }
    updateClocks();
    setInterval(updateClocks, 30000);
    if (panel) {
        setInterval(checkAlerts, 30000);
        document.addEventListener('visibilitychange', () => { if (!document.hidden) { checkAlerts(); updateClocks(); } });
    }
})();
