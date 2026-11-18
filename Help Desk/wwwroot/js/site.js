document.addEventListener("submit", event => { const form = event.target; if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-loading-form")) return; const button = form.querySelector("button[type=submit]"); if (!button || !form.checkValidity()) return; button.disabled = true; button.dataset.originalText = button.textContent; button.innerHTML = "<span class=\"submit-spinner\" aria-hidden=\"true\"></span> Sending…"; });
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
        const select = event.target;
        if (select instanceof HTMLSelectElement && select.closest("form[data-auto-save-agents]")) {
            const form = select.closest("form[data-auto-save-agents]");
            const selectedName = select.closest(".customer-team-level")?.querySelector(".customer-selected-agent");
            if (selectedName) selectedName.textContent = select.value ? select.selectedOptions[0].textContent : "No agent assigned";
            clearTimeout(form._agentSaveTimer);
            form._agentSaveTimer = setTimeout(() => saveAgentAssignments(form), 250);
            return;
        }
        const checkbox = event.target;
        if (!(checkbox instanceof HTMLInputElement) || checkbox.type !== "checkbox") return;
        const picker = checkbox.closest("[data-agent-picker]");
        if (picker) {
            const count = picker.querySelectorAll(".cu-agent-picker-menu input[type=checkbox]:checked").length;
            const summary = picker.querySelector("[data-agent-picker-count]");
            if (summary) summary.textContent = count === 0 ? "Select agents" : `${count} selected`;
        }
        const teamForm = checkbox.closest("form.customer-team-details");
        if (teamForm) {
            const levelCount = teamForm.querySelectorAll(`input[name="${checkbox.name}"]:checked`).length;
            const count = teamForm.querySelector(`[data-team-count="${checkbox.name}"]`);
            if (count) count.textContent = `${levelCount} selected`;
        }
        const form = checkbox.closest("form[data-auto-save-agents]");
        if (form) {
            clearTimeout(form._agentSaveTimer);
            form._agentSaveTimer = setTimeout(() => saveAgentAssignments(form), 250);
        }
    });

    async function saveAgentAssignments(form) {
        if (form._agentSaving) { form._agentSaveQueued = true; return; }
        form._agentSaving = true;
        const status = form.querySelector("[data-auto-save-status]") || form.closest("[data-tat-alerts], .customer-support-panel")?.querySelector("[data-auto-save-status]");
        if (status) status.textContent = "Saving…";
        try {
            const response = await fetch(form.action, { method: "POST", body: new FormData(form), headers: { "X-Requested-With": "XMLHttpRequest", Accept: "application/json" } });
            const result = await response.json();
            if (!response.ok) throw new Error(result.message || "Could not save selections.");
            if (status) status.textContent = result.message || "Saved";
        } catch (error) {
            if (status) status.textContent = error.message || "Could not save. Please try again.";
        } finally {
            form._agentSaving = false;
            if (form._agentSaveQueued) { form._agentSaveQueued = false; saveAgentAssignments(form); }
        }
    }

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

    // Keep ticket replies compact until content needs more room.
    document.querySelectorAll("textarea[data-auto-grow]").forEach(textarea => {
        const minHeight = Number(textarea.dataset.minHeight) || 42;
        const maxHeight = Number(textarea.dataset.maxHeight) || 220;
        const resize = () => {
            textarea.style.height = `${minHeight}px`;
            textarea.style.height = `${Math.min(textarea.scrollHeight, maxHeight)}px`;
            textarea.style.overflowY = textarea.scrollHeight > maxHeight ? "auto" : "hidden";
        };
        textarea.addEventListener("input", resize);
        resize();
    });

    document.querySelectorAll("form[data-auto-submit] select").forEach(select => {
        select.addEventListener("change", () => select.form.requestSubmit());
    });

