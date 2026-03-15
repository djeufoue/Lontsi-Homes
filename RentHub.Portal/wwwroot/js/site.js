(() => {
  const body = document.body;
  const toggle = document.getElementById("rhSidebarToggle");
  const confirmationModalEl = document.getElementById("confirmationModal");
  const confirmationTitleEl = document.getElementById("confirmationModalTitle");
  const confirmationMessageEl = document.getElementById("confirmationModalMessage");
  const confirmationProceedEl = document.getElementById("confirmationModalProceed");

  const workspaceState = {
    connection: null,
    activePropertyId: null,
    propertyRefreshPromise: null
  };

  const escapeHtml = (value) => {
    const div = document.createElement("div");
    div.textContent = value ?? "";
    return div.innerHTML;
  };

  const getBootstrapModal = (element) => {
    if (!element || typeof bootstrap === "undefined") {
      return null;
    }

    return bootstrap.Modal.getOrCreateInstance(element);
  };

  const ensureFeedbackModal = () => {
    let modalEl = document.getElementById("rhFeedbackModal");
    if (modalEl) {
      return modalEl;
    }

    modalEl = document.createElement("div");
    modalEl.id = "rhFeedbackModal";
    modalEl.className = "modal fade";
    modalEl.tabIndex = -1;
    modalEl.setAttribute("aria-hidden", "true");
    modalEl.innerHTML = `
      <div class="modal-dialog modal-dialog-centered">
        <div class="modal-content rh-success-modal" data-feedback-surface="true">
          <div class="modal-header border-0">
            <h5 class="modal-title" id="rhFeedbackModalTitle">Action completed</h5>
            <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
          </div>
          <div class="modal-body pt-0">
            <p class="mb-0" id="rhFeedbackModalMessage"></p>
          </div>
          <div class="modal-footer border-0 pt-0">
            <button type="button" class="btn rh-btn-subtle" data-bs-dismiss="modal" id="rhFeedbackModalClose">Close</button>
          </div>
        </div>
      </div>`;

    document.body.appendChild(modalEl);
    return modalEl;
  };

  const showFeedbackModal = (type, message, options = {}) => {
    const modalEl = ensureFeedbackModal();
    const modal = getBootstrapModal(modalEl);
    const titleEl = modalEl.querySelector("#rhFeedbackModalTitle");
    const messageEl = modalEl.querySelector("#rhFeedbackModalMessage");
    const surfaceEl = modalEl.querySelector("[data-feedback-surface='true']");
    const closeButtonEl = modalEl.querySelector(".btn-close");
    const footerEl = modalEl.querySelector(".modal-footer");

    if (!modal || !titleEl || !messageEl || !surfaceEl || !closeButtonEl || !footerEl) {
      return;
    }

    const allowManualClose = options.allowManualClose !== false;
    const autoCloseEnabled = options.autoCloseEnabled === true;
    const autoCloseMs = Number(options.autoCloseSeconds || 0) * 1000;

    titleEl.textContent = type === "error" ? "Action Failed" : "Action Completed";
    messageEl.textContent = message;
    surfaceEl.classList.toggle("rh-error-modal", type === "error");
    surfaceEl.classList.toggle("rh-success-modal", type !== "error");
    closeButtonEl.style.display = allowManualClose ? "" : "none";
    footerEl.style.display = allowManualClose ? "" : "none";

    const instance = new bootstrap.Modal(modalEl, {
      backdrop: allowManualClose ? true : "static",
      keyboard: allowManualClose
    });

    instance.show();

    if (autoCloseEnabled && autoCloseMs > 0) {
      window.setTimeout(() => {
        instance.hide();
      }, autoCloseMs);
    }
  };

  const parseAjaxMessage = async (response, fallback) => {
    try {
      const payload = await response.json();
      return payload?.message || payload?.Message || fallback;
    } catch {
      try {
        const text = await response.text();
        return text || fallback;
      } catch {
        return fallback;
      }
    }
  };

  const bindAutoSearchForms = (root) => {
    root.querySelectorAll(".rh-auto-search-form").forEach((form) => {
      if (form.dataset.autoSearchBound === "true") {
        return;
      }

      const input = form.querySelector(".js-auto-submit");
      if (!input) {
        return;
      }

      form.dataset.autoSearchBound = "true";
      let timer = null;
      input.addEventListener("input", () => {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => {
          const propertyRoot = form.closest("[data-property-overview-root='true']");
          if (propertyRoot && form.dataset.sectionSearch) {
            const payload = Object.fromEntries(new FormData(form).entries());
            refreshPropertyOverview(propertyRoot, {
              sectionName: form.dataset.sectionSearch,
              overrides: payload
            }).catch(() => {
              showFeedbackModal("error", "Unable to refresh this section right now.", getDialogOptions(propertyRoot));
            });
            return;
          }

          const apartmentRoot = form.closest("[data-apartment-overview-root='true']");
          if (apartmentRoot && form.dataset.sectionSearch) {
            const payload = Object.fromEntries(new FormData(form).entries());
            refreshApartmentOverview(apartmentRoot, {
              sectionName: form.dataset.sectionSearch,
              overrides: payload
            }).catch(() => {
              showFeedbackModal("error", "Unable to refresh this section right now.", getDialogOptions(apartmentRoot));
            });
            return;
          }

          form.requestSubmit();
        }, 320);
      });
    });
  };
  const initPropertyImageUpload = (root) => {
    const imageButton = root.querySelector("[data-image-trigger]");
    const imageInput = root.querySelector("#propertyImageFile");
    const imageForm = root.querySelector("#propertyImageForm");
    const validation = root.querySelector("#propertyImageValidation");

    const setValidation = (message) => {
      if (!validation) {
        return;
      }

      validation.textContent = message || "";
      validation.classList.toggle("is-visible", !!message);
    };

    if (imageButton && imageInput && imageButton.dataset.bound !== "true") {
      imageButton.dataset.bound = "true";
      imageButton.addEventListener("click", () => imageInput.click());
    }

    if (!imageInput || !imageForm || imageInput.dataset.bound === "true") {
      return;
    }

    imageInput.dataset.bound = "true";
    imageInput.addEventListener("change", () => {
      const file = imageInput.files && imageInput.files[0];
      if (!file) {
        return;
      }

      setValidation("");

      if (!file.type.startsWith("image/")) {
        setValidation("Please choose an image file.");
        imageInput.value = "";
        return;
      }

      if (file.size > 2 * 1024 * 1024) {
        setValidation("Property images must be 2 MB or smaller.");
        imageInput.value = "";
        return;
      }

      const objectUrl = URL.createObjectURL(file);
      const img = new Image();
      img.onload = () => {
        if (img.width <= img.height) {
          setValidation("Please choose a landscape image.");
          imageInput.value = "";
          URL.revokeObjectURL(objectUrl);
          return;
        }

        URL.revokeObjectURL(objectUrl);
        imageForm.requestSubmit();
      };
      img.onerror = () => {
        setValidation("This image could not be validated.");
        imageInput.value = "";
        URL.revokeObjectURL(objectUrl);
      };
      img.src = objectUrl;
    });
  };

  const initPropertySettingsEditor = (root) => {
    const propertySettingsForm = root.querySelector("#propertySettingsForm");
    const propertySettingsToggle = root.querySelector("#propertySettingsEditToggle");
    const propertySettingsCancel = root.querySelector("#propertySettingsCancel");
    const autoCloseEnabled = root.querySelector("#autoCloseEnabled");
    const autoCloseSeconds = root.querySelector("#autoCloseSeconds");

    const syncAutoCloseState = () => {
      if (!autoCloseEnabled || !autoCloseSeconds) {
        return;
      }

      autoCloseSeconds.disabled = !autoCloseEnabled.checked;
    };

    if (autoCloseEnabled && autoCloseEnabled.dataset.bound !== "true") {
      autoCloseEnabled.dataset.bound = "true";
      autoCloseEnabled.addEventListener("change", syncAutoCloseState);
    }
    syncAutoCloseState();

    if (!propertySettingsForm) {
      return;
    }

    const setPropertySettingsMode = (isEditing) => {
      propertySettingsForm.dataset.inlineEditor = isEditing ? "editing" : "locked";
      propertySettingsForm.querySelectorAll("input[name], textarea[name]").forEach((field) => {
        if (field.name === "__RequestVerificationToken" || field.name === "propertyId") {
          return;
        }

        field.readOnly = !isEditing;
      });

      const actionRow = propertySettingsForm.querySelector(".rh-inline-editor-actions");
      if (actionRow) {
        actionRow.hidden = !isEditing;
      }
    };

    if (propertySettingsToggle && propertySettingsToggle.dataset.bound !== "true") {
      propertySettingsToggle.dataset.bound = "true";
      propertySettingsToggle.addEventListener("click", () => setPropertySettingsMode(true));
    }

    if (propertySettingsCancel && propertySettingsCancel.dataset.bound !== "true") {
      propertySettingsCancel.dataset.bound = "true";
      propertySettingsCancel.addEventListener("click", () => {
        propertySettingsForm.reset();
        setPropertySettingsMode(false);
      });
    }

    setPropertySettingsMode(false);
  };

  const syncPropertyRootDataset = (currentRoot, nextRoot) => {
    ["propertyId", "overviewContentUrl", "showCloseButton", "autoCloseEnabled", "autoCloseSeconds"].forEach((key) => {
      if (nextRoot.dataset[key] !== undefined) {
        currentRoot.dataset[key] = nextRoot.dataset[key];
      }
    });
  };

  const buildRefreshUrl = (root, overrides = {}) => {
    const refreshUrl = new URL(root.dataset.overviewContentUrl, window.location.origin);
    const defaults = {
      apartmentSearch: root.querySelector("form[data-section-search='apartments'] input[name='apartmentSearch']")?.value ?? "",
      memberSearch: root.querySelector("form[data-section-search='members'] input[name='memberSearch']")?.value ?? "",
      unitsPage: root.querySelector("[data-property-section='apartments'] input[name='unitsPage']")?.value ?? "1",
      unitsPageSize: root.querySelector("[data-property-section='apartments'] input[name='unitsPageSize']")?.value ?? "6"
    };

    Object.entries({ ...defaults, ...overrides }).forEach(([key, value]) => {
      if (value !== undefined && value !== null) {
        refreshUrl.searchParams.set(key, value);
      }
    });

    return refreshUrl;
  };

  const refreshPropertyOverview = async (root, options = {}) => {
    if (!root || workspaceState.propertyRefreshPromise) {
      return workspaceState.propertyRefreshPromise;
    }

    workspaceState.propertyRefreshPromise = (async () => {
      const response = await fetch(buildRefreshUrl(root, options.overrides || {}), {
        headers: {
          "X-Requested-With": "XMLHttpRequest"
        },
        credentials: "same-origin"
      });

      if (!response.ok) {
        throw new Error("Unable to refresh the property workspace right now.");
      }

      const html = await response.text();
      const parser = new DOMParser();
      const doc = parser.parseFromString(html, "text/html");
      const nextRoot = doc.querySelector("[data-property-overview-root='true']");
      if (!nextRoot) {
        throw new Error("Property workspace content was not returned.");
      }

      if (options.sectionName) {
        const currentSection = root.querySelector(`[data-property-section='${options.sectionName}']`);
        const nextSection = nextRoot.querySelector(`[data-property-section='${options.sectionName}']`);
        if (!currentSection || !nextSection) {
          throw new Error("Requested property section could not be refreshed.");
        }

        currentSection.replaceWith(nextSection);
        syncPropertyRootDataset(root, nextRoot);
        initPropertyOverview(root);
        return;
      }

      root.replaceWith(nextRoot);
      initPropertyOverview(nextRoot);
    })();

    try {
      await workspaceState.propertyRefreshPromise;
    } finally {
      workspaceState.propertyRefreshPromise = null;
    }
  };

  const buildApartmentRefreshUrl = (root, overrides = {}) => {
    const refreshUrl = new URL(root.dataset.overviewContentUrl, window.location.origin);
    const defaults = {
      tenancySearch: root.querySelector("form[data-section-search='tenancies'] input[name='tenancySearch']")?.value ?? "",
      memberSearch: root.querySelector("form[data-section-search='members'] input[name='memberSearch']")?.value ?? ""
    };

    Object.entries({ ...defaults, ...overrides }).forEach(([key, value]) => {
      if (value !== undefined && value !== null) {
        refreshUrl.searchParams.set(key, value);
      }
    });

    return refreshUrl;
  };

  const refreshApartmentOverview = async (root, options = {}) => {
    if (!root || root.dataset.refreshing === "true") {
      return;
    }

    root.dataset.refreshing = "true";
    try {
      const response = await fetch(buildApartmentRefreshUrl(root, options.overrides || {}), {
        headers: {
          "X-Requested-With": "XMLHttpRequest"
        },
        credentials: "same-origin"
      });

      if (!response.ok) {
        throw new Error("Unable to refresh the apartment workspace right now.");
      }

      const html = await response.text();
      const parser = new DOMParser();
      const doc = parser.parseFromString(html, "text/html");
      const nextRoot = doc.querySelector("[data-apartment-overview-root='true']");
      if (!nextRoot) {
        throw new Error("Apartment workspace content was not returned.");
      }

      if (options.sectionName) {
        const currentSection = root.querySelector(`[data-apartment-section='${options.sectionName}']`);
        const nextSection = nextRoot.querySelector(`[data-apartment-section='${options.sectionName}']`);
        if (!currentSection || !nextSection) {
          throw new Error("Requested apartment section could not be refreshed.");
        }

        currentSection.replaceWith(nextSection);
        initApartmentOverview(root);
        return;
      }

      root.replaceWith(nextRoot);
      initApartmentOverview(nextRoot);
    } finally {
      delete root.dataset.refreshing;
    }
  };

  const getDialogOptions = (root) => ({
    allowManualClose: true,
    autoCloseEnabled: root?.dataset.autoCloseEnabled === "true",
    autoCloseSeconds: Number(root?.dataset.autoCloseSeconds || 0)
  });

  const getDialogOptionsForForm = (root, form) => {
    const defaults = getDialogOptions(root);
    if (!form?.action?.includes("UpdateSuccessDialogSettings")) {
      return defaults;
    }

    const formData = new FormData(form);
    const autoCloseEnabled = formData.get("autoCloseEnabled") === "true";
    const autoCloseSeconds = Number(formData.get("autoCloseSeconds") || defaults.autoCloseSeconds || 0);

    root.dataset.autoCloseEnabled = autoCloseEnabled ? "true" : "false";
    root.dataset.autoCloseSeconds = String(autoCloseSeconds);

    return {
      allowManualClose: true,
      autoCloseEnabled,
      autoCloseSeconds
    };
  };
  const initLivePropertyForms = (root) => {
    root.querySelectorAll("form[data-live-submit='property-overview']").forEach((form) => {
      if (form.dataset.liveBound === "true") {
        return;
      }

      form.dataset.liveBound = "true";
      form.addEventListener("submit", async (event) => {
        event.preventDefault();

        const submitter = event.submitter;
        if (submitter) {
          submitter.disabled = true;
        }

        try {
          const dialogOptions = getDialogOptionsForForm(root, form);
          const response = await fetch(form.action, {
            method: (form.method || "POST").toUpperCase(),
            body: new FormData(form),
            headers: {
              "X-Requested-With": "XMLHttpRequest"
            },
            credentials: "same-origin"
          });

          if (!response.ok) {
            const message = await parseAjaxMessage(response, "Unable to complete this action right now.");
            showFeedbackModal("error", message, dialogOptions);
            return;
          }

          const message = await parseAjaxMessage(response, "Action completed successfully.");
          if (form.dataset.liveCloseModal === "true") {
            const modalEl = form.closest(".modal");
            if (modalEl) {
              const modal = getBootstrapModal(modalEl);
              modal?.hide();
            }
          }

          showFeedbackModal("success", message, dialogOptions);
          await refreshPropertyOverview(root);
        } catch (error) {
          showFeedbackModal("error", error?.message || "Unable to complete this action right now.", getDialogOptions(root));
        } finally {
          if (submitter) {
            submitter.disabled = false;
          }
        }
      });
    });
  };

  const ensureWorkspaceHub = async (root) => {
    const propertyId = Number(root?.dataset.propertyId || 0);
    if (!propertyId || !window.signalR) {
      return;
    }

    if (!workspaceState.connection) {
      workspaceState.connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/workspace")
        .withAutomaticReconnect()
        .build();

      workspaceState.connection.on("PropertyUpdated", async (payload) => {
        const currentRoot = document.querySelector("[data-property-overview-root='true']");
        const currentPropertyId = Number(currentRoot?.dataset.propertyId || 0);
        if (!payload || Number(payload.propertyId) !== currentPropertyId || !currentRoot) {
          return;
        }

        try {
          await refreshPropertyOverview(currentRoot);
        } catch {
          // Keep the current page usable even if live refresh fails.
        }
      });

      await workspaceState.connection.start();
    }

    if (workspaceState.activePropertyId && workspaceState.activePropertyId !== propertyId) {
      await workspaceState.connection.invoke("LeavePropertyGroup", workspaceState.activePropertyId);
    }

    if (workspaceState.activePropertyId !== propertyId) {
      await workspaceState.connection.invoke("JoinPropertyGroup", propertyId);
      workspaceState.activePropertyId = propertyId;
    }
  };

  const initPropertyOverview = (root = document.querySelector("[data-property-overview-root='true']")) => {
    if (!root) {
      return;
    }

    bindAutoSearchForms(root);
    initPropertyImageUpload(root);
    initPropertySettingsEditor(root);
    initLivePropertyForms(root);
    ensureWorkspaceHub(root).catch(() => {
      // SignalR enhances live sync, but the page should still work without it.
    });
  };

  const initApartmentGallery = (root) => {
    const gallery = root.querySelector("[data-apartment-gallery='true']");
    if (!gallery || gallery.dataset.bound === "true") {
      return;
    }

    const slides = Array.from(gallery.querySelectorAll("[data-gallery-slide]"));
    if (!slides.length) {
      return;
    }

    const currentName = gallery.querySelector("[data-gallery-current-name]");
    const currentIndex = gallery.querySelector("[data-gallery-current-index]");
    const manageOpen = root.querySelector("[data-apartment-image-manage-open='true']");
    const manageModal = document.getElementById("manageApartmentImageModal");
    const hiddenDocumentId = manageModal?.querySelector("input[name='currentDocumentId']");
    const deleteDocumentId = manageModal?.querySelector("input[name='documentId']");
    const imageNameTarget = manageModal?.querySelector("[data-current-image-name]");
    const lightboxPreview = document.getElementById("apartmentImageLightboxPreview");
    let activeIndex = Math.max(0, slides.findIndex((slide) => slide.classList.contains("is-active")));

    const updateManageModal = () => {
      const activeSlide = slides[activeIndex];
      const documentId = activeSlide?.dataset.documentId || "";
      const imageName = activeSlide?.dataset.imageName || "Current apartment image";
      if (hiddenDocumentId) hiddenDocumentId.value = documentId;
      if (deleteDocumentId) deleteDocumentId.value = documentId;
      if (imageNameTarget) imageNameTarget.textContent = imageName;
      if (lightboxPreview) {
        lightboxPreview.alt = imageName;
      }
    };

    const updateSlide = (nextIndex) => {
      activeIndex = (nextIndex + slides.length) % slides.length;
      slides.forEach((slide, index) => {
        slide.classList.toggle("is-active", index === activeIndex);
      });

      if (currentName) {
        currentName.textContent = slides[activeIndex].dataset.imageName || "Apartment image";
      }

      if (currentIndex) {
        currentIndex.textContent = String(activeIndex + 1);
      }

      if (lightboxPreview) {
        lightboxPreview.src = slides[activeIndex].dataset.imageUrl || "";
        lightboxPreview.alt = slides[activeIndex].dataset.imageName || "Apartment image preview";
      }

      updateManageModal();
    };

    gallery.querySelector("[data-gallery-nav='prev']")?.addEventListener("click", () => updateSlide(activeIndex - 1));
    gallery.querySelector("[data-gallery-nav='next']")?.addEventListener("click", () => updateSlide(activeIndex + 1));
    manageOpen?.addEventListener("click", updateManageModal);
    gallery.querySelectorAll("[data-image-lightbox-trigger='true']").forEach((trigger) => {
      if (trigger.dataset.bound === "true") {
        return;
      }

      trigger.dataset.bound = "true";
      trigger.addEventListener("click", () => {
        const imageUrl = trigger.dataset.imageUrl || slides[activeIndex]?.dataset.imageUrl || "";
        const imageName = trigger.dataset.imageName || slides[activeIndex]?.dataset.imageName || "Apartment image preview";
        if (lightboxPreview) {
          lightboxPreview.src = imageUrl;
          lightboxPreview.alt = imageName;
        }
      });
    });
    updateSlide(activeIndex);
    gallery.dataset.bound = "true";
  };

  const initApartmentReminderEditor = (root) => {
    const form = root.querySelector("#apartmentReminderForm");
    if (!form) {
      return;
    }

    const setMode = (isEditing) => {
      form.dataset.inlineEditor = isEditing ? "editing" : "locked";
      form.querySelectorAll("input[name]").forEach((field) => {
        if (field.name === "__RequestVerificationToken" || field.name === "apartmentId") {
          return;
        }

        field.readOnly = !isEditing;
      });

      const actionRow = form.querySelector(".rh-inline-editor-actions");
      if (actionRow) {
        actionRow.hidden = !isEditing;
      }
    };

    const editToggle = root.querySelector("#apartmentReminderEditToggle");
    const cancelButton = root.querySelector("#apartmentReminderCancel");

    if (editToggle && editToggle.dataset.bound !== "true") {
      editToggle.dataset.bound = "true";
      editToggle.addEventListener("click", () => setMode(true));
    }

    if (cancelButton && cancelButton.dataset.bound !== "true") {
      cancelButton.dataset.bound = "true";
      cancelButton.addEventListener("click", () => {
        form.reset();
        setMode(false);
      });
    }

    setMode(false);
  };

  const initApartmentOverview = (root = document.querySelector("[data-apartment-overview-root='true']")) => {
    if (!root) {
      return;
    }

    bindAutoSearchForms(root);
    initApartmentGallery(root);
    initApartmentReminderEditor(root);
  };

  const initSessionKeepAlive = () => {
    if (!window.rhSession?.keepAliveUrl) {
      return;
    }

    let lastActivityAt = Date.now();
    let lastPingAt = 0;
    let pingInFlight = false;
    const activeWindowMs = 5 * 60 * 1000;
    const pingIntervalMs = 4 * 60 * 1000;

    const markActivity = () => {
      lastActivityAt = Date.now();
    };

    const sendKeepAlive = async () => {
      if (pingInFlight) {
        return;
      }

      pingInFlight = true;
      try {
        const response = await fetch(window.rhSession.keepAliveUrl, {
          method: "POST",
          headers: {
            "X-Requested-With": "XMLHttpRequest"
          },
          credentials: "same-origin"
        });

        if (response.status === 401) {
          window.location.href = "/Auth/Login";
          return;
        }

        if (response.ok) {
          lastPingAt = Date.now();
        }
      } catch {
        // Silent by design; we only redirect on confirmed auth expiry.
      } finally {
        pingInFlight = false;
      }
    };

    ["click", "scroll", "keydown", "mousemove", "touchstart", "visibilitychange"].forEach((eventName) => {
      document.addEventListener(eventName, markActivity, { passive: true });
    });

    window.setTimeout(() => {
      if (!document.hidden) {
        sendKeepAlive();
      }
    }, 1500);

    window.setInterval(() => {
      const now = Date.now();
      if (document.hidden) {
        return;
      }

      if (now - lastActivityAt > activeWindowMs) {
        return;
      }

      if (now - lastPingAt < pingIntervalMs) {
        return;
      }

      sendKeepAlive();
    }, 60 * 1000);
  };

  if (toggle) {
    toggle.addEventListener("click", () => {
      body.classList.toggle("sidebar-open");
    });
  }

  if (confirmationModalEl && confirmationProceedEl) {
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
  }

  initPropertyOverview();
  initApartmentOverview();
  bindAutoSearchForms(document);
  initSessionKeepAlive();
})();


