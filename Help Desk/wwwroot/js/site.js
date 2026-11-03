(() => {
    const dialog = document.getElementById("delete-confirm-dialog");
    if (!dialog) return;

    const title = dialog.querySelector("#delete-confirm-title");
    const message = dialog.querySelector("#delete-confirm-message");
    const accept = dialog.querySelector("[data-confirm-accept]");
    let pendingForm = null;

    document.addEventListener("submit", event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-confirm-delete")) return;
        if (form.dataset.confirmed === "true") {
            delete form.dataset.confirmed;
            return;
        }

        event.preventDefault();
        pendingForm = form;
        title.textContent = form.dataset.confirmTitle || "Confirm deletion";
        message.textContent = form.dataset.confirmMessage || "Are you sure you want to delete this item?";
        accept.textContent = form.dataset.confirmAction || "Delete";
        dialog.returnValue = "";
        dialog.showModal();
    });

    dialog.addEventListener("close", () => {
        const confirmed = dialog.returnValue === "confirm";
        const form = pendingForm;
        pendingForm = null;
        if (!confirmed || !form?.isConnected) return;
        form.dataset.confirmed = "true";
        form.requestSubmit();
    });

    function positionAgentMenu(picker) {
        const trigger = picker.querySelector("[data-agent-picker-trigger]");
        const menu = picker.querySelector(".cu-agent-picker-menu");
        if (!trigger || !menu || (!menu.matches(":popover-open") && !menu.hasAttribute("data-open"))) return;
        const rect = trigger.getBoundingClientRect();
        const width = Math.min(Math.max(rect.width, 220), window.innerWidth - 24);
        menu.style.width = `${width}px`;
        const height = Math.min(menu.scrollHeight, window.innerHeight - 24, 320);
        const left = Math.max(12, Math.min(rect.left, window.innerWidth - width - 12));
        let top = rect.bottom + 6;
        if (top + height > window.innerHeight - 12) top = Math.max(12, rect.top - height - 6);
        menu.style.left = `${left}px`;
        menu.style.top = `${top}px`;
    }

    document.addEventListener("click", event => {
        const trigger = event.target.closest?.("[data-agent-picker-trigger]");
        const targetPicker = event.target.closest?.("[data-agent-picker]");
        document.querySelectorAll(".cu-agent-picker-menu").forEach(openMenu => {
            if (openMenu.closest("[data-agent-picker]") === targetPicker) return;
            if (openMenu.matches(":popover-open") || openMenu.hasAttribute("data-open")) {
                if (openMenu.hidePopover) openMenu.hidePopover();
                openMenu.removeAttribute("data-open");
                openMenu.closest("[data-agent-picker]")?.querySelector("[data-agent-picker-trigger]")?.setAttribute("aria-expanded", "false");
            }
        });
        if (!trigger) return;
        const picker = trigger.closest("[data-agent-picker]");
        const menu = picker?.querySelector(".cu-agent-picker-menu");
        if (!picker || !menu) return;
        if (menu.matches(":popover-open") || menu.hasAttribute("data-open")) {
            if (menu.hidePopover) menu.hidePopover();
            menu.removeAttribute("data-open");
            trigger.setAttribute("aria-expanded", "false");
            return;
        }
        if (menu.showPopover) menu.showPopover();
        else menu.setAttribute("data-open", "true");
        trigger.setAttribute("aria-expanded", "true");
        positionAgentMenu(picker);
    });

    document.addEventListener("keydown", event => {
        if (event.key !== "Escape") return;
        document.querySelectorAll(".cu-agent-picker-menu").forEach(menu => {
            if (!menu.matches(":popover-open") && !menu.hasAttribute("data-open")) return;
            if (menu.hidePopover) menu.hidePopover();
            menu.removeAttribute("data-open");
            const trigger = menu.closest("[data-agent-picker]")?.querySelector("[data-agent-picker-trigger]");
            trigger?.setAttribute("aria-expanded", "false");
            trigger?.focus();
        });
    });

    document.addEventListener("change", event => {
        const checkbox = event.target;
        if (!(checkbox instanceof HTMLInputElement) || checkbox.type !== "checkbox" || !checkbox.closest("[data-agent-picker]")) return;
        const picker = checkbox.closest("[data-agent-picker]");
        const count = picker.querySelectorAll(".cu-agent-picker-menu input[type=checkbox]:checked").length;
        const summary = picker.querySelector("[data-agent-picker-count]");
        if (summary) summary.textContent = count === 0 ? "Select agents" : `${count} selected`;
    });

    document.querySelectorAll(".cu-agent-picker-menu[popover]").forEach(menu => {
        menu.addEventListener("toggle", () => {
            const picker = menu.closest("[data-agent-picker]");
            picker?.querySelector("[data-agent-picker-trigger]")?.setAttribute("aria-expanded", String(menu.matches(":popover-open")));
        });
    });

    const repositionOpenAgentMenus = () => document.querySelectorAll("[data-agent-picker]").forEach(positionAgentMenu);
    window.addEventListener("resize", repositionOpenAgentMenus);
    window.addEventListener("scroll", repositionOpenAgentMenus, true);
})();
