(() => {
  const body = document.body;
  const toggle = document.getElementById("rhSidebarToggle");
  const confirmationModalEl = document.getElementById("confirmationModal");
  const confirmationTitleEl = document.getElementById("confirmationModalTitle");
  const confirmationMessageEl = document.getElementById("confirmationModalMessage");
  const confirmationProceedEl = document.getElementById("confirmationModalProceed");

  if (toggle) {
    toggle.addEventListener("click", () => {
      body.classList.toggle("sidebar-open");
    });
  }

  if (!confirmationModalEl || !confirmationProceedEl) {
    return;
  }

  const confirmationModal = new bootstrap.Modal(confirmationModalEl);
  let pendingForm = null;

  document.addEventListener("submit", (event) => {
    const form = event.target.closest("form[data-confirm-modal='true']");
    if (!form) {
      return;
    }

    if (form.dataset.confirmed === "true") {
      form.dataset.confirmed = "false";
      return;
    }

    event.preventDefault();
    pendingForm = form;

    if (confirmationTitleEl) {
      confirmationTitleEl.textContent = form.dataset.confirmTitle || "Please confirm";
    }

    if (confirmationMessageEl) {
      confirmationMessageEl.textContent = form.dataset.confirmMessage || "Are you sure you want to continue?";
    }

    confirmationProceedEl.textContent = form.dataset.confirmProceed || "Proceed";
    confirmationProceedEl.classList.toggle("rh-btn-primary", form.dataset.confirmStyle === "primary");
    confirmationProceedEl.classList.toggle("rh-btn-outline-danger", form.dataset.confirmStyle !== "primary");

    confirmationModal.show();
  });

  confirmationProceedEl.addEventListener("click", () => {
    if (!pendingForm) {
      return;
    }

    pendingForm.dataset.confirmed = "true";
    confirmationModal.hide();
    pendingForm.requestSubmit();
    pendingForm = null;
  });

  confirmationModalEl.addEventListener("hidden.bs.modal", () => {
    pendingForm = null;
  });
})();
