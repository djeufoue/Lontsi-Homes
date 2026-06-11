(() => {
  const body = document.body;
  const toggle = document.getElementById("rhSidebarToggle");
  const sidebar = document.getElementById("rhSidebar");
  const sidebarClose = document.getElementById("rhSidebarClose");
  const sidebarScrim = document.getElementById("rhSidebarScrim");
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

  const initCountrySelectors = (root = document) => {
    root.querySelectorAll("[data-country-selector]").forEach((select) => {
      if (select.dataset.countrySelectorBound === "true") {
        return;
      }

      const form = select.closest("form") || root;
      const output = form.querySelector("[data-country-code-output]");
      const syncCountryCode = () => {
        const selectedOption = select.options[select.selectedIndex];
        if (output) {
          output.value = selectedOption?.dataset?.countryCode || "";
        }
      };

      select.dataset.countrySelectorBound = "true";
      select.addEventListener("change", syncCountryCode);
      syncCountryCode();
    });
  };

  const getBootstrapModal = (element) => {
    if (!element || typeof bootstrap === "undefined") {
      return null;
    }

    return bootstrap.Modal.getOrCreateInstance(element);
  };

  document.addEventListener("hide.bs.modal", (event) => {
    const activeElement = document.activeElement;
    if (activeElement && event.target.contains(activeElement)) {
      activeElement.blur();
    }
  });

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

  const datePickerState = {
    activeInput: null,
    activeMonth: null,
    popover: null
  };

  const initDatePickers = (root = document) => {
    const dateInputs = Array.from(root.querySelectorAll("input[type='date']"));
    if (!dateInputs.length) {
      return;
    }

    datePickerState.popover = document.getElementById("rhDatePickerPopover");

    const pad = (value) => String(value).padStart(2, "0");
    const toIsoDate = (date) => `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
    const parseIsoDate = (value) => {
      const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value || "");
      if (!match) {
        return null;
      }

      const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
      return Number.isNaN(date.getTime()) ? null : date;
    };

    const sameDay = (first, second) =>
      first && second && first.getFullYear() === second.getFullYear() && first.getMonth() === second.getMonth() && first.getDate() === second.getDate();

    const ensurePopover = () => {
      if (datePickerState.popover) {
        return datePickerState.popover;
      }

      datePickerState.popover = document.createElement("div");
      datePickerState.popover.id = "rhDatePickerPopover";
      datePickerState.popover.className = "rh-date-popover";
      datePickerState.popover.setAttribute("role", "dialog");
      datePickerState.popover.setAttribute("aria-label", "Choose date");
      document.body.appendChild(datePickerState.popover);
      return datePickerState.popover;
    };

    const closeDatePicker = () => {
      if (!datePickerState.popover) {
        return;
      }

      datePickerState.popover.hidden = true;
      datePickerState.activeInput = null;
      datePickerState.activeMonth = null;
    };

    const positionPopover = () => {
      if (!datePickerState.activeInput || !datePickerState.popover || datePickerState.popover.hidden) {
        return;
      }

      const wrapper = datePickerState.activeInput.closest(".rh-date-field") || datePickerState.activeInput;
      const rect = wrapper.getBoundingClientRect();
      const spacing = 8;
      const popoverWidth = datePickerState.popover.offsetWidth || 330;
      const left = Math.min(
        Math.max(window.scrollX + rect.left, window.scrollX + 12),
        window.scrollX + window.innerWidth - popoverWidth - 12
      );

      datePickerState.popover.style.left = `${left}px`;
      datePickerState.popover.style.top = `${window.scrollY + rect.bottom + spacing}px`;
    };

    const renderDatePicker = () => {
      if (!datePickerState.activeInput || !datePickerState.activeMonth) {
        return;
      }

      const selected = parseIsoDate(datePickerState.activeInput.value);
      const min = parseIsoDate(datePickerState.activeInput.min);
      const max = parseIsoDate(datePickerState.activeInput.max);
      const today = new Date();
      const monthStart = new Date(datePickerState.activeMonth.getFullYear(), datePickerState.activeMonth.getMonth(), 1);
      const monthEnd = new Date(datePickerState.activeMonth.getFullYear(), datePickerState.activeMonth.getMonth() + 1, 0);
      const firstWeekday = monthStart.getDay();
      const daysInMonth = monthEnd.getDate();
      const monthLabel = new Intl.DateTimeFormat(undefined, { month: "long", year: "numeric" }).format(monthStart);
      const days = [];

      for (let index = 0; index < firstWeekday; index += 1) {
        days.push(`<span class="rh-date-day is-empty" aria-hidden="true"></span>`);
      }

      for (let day = 1; day <= daysInMonth; day += 1) {
        const date = new Date(datePickerState.activeMonth.getFullYear(), datePickerState.activeMonth.getMonth(), day);
        const iso = toIsoDate(date);
        const isDisabled = (min && date < min) || (max && date > max);
        const classes = [
          "rh-date-day",
          sameDay(date, selected) ? "is-selected" : "",
          sameDay(date, today) ? "is-today" : ""
        ].filter(Boolean).join(" ");

        days.push(`
          <button type="button" class="${classes}" data-date-value="${iso}" ${isDisabled ? "disabled" : ""}>
            ${day}
          </button>`);
      }

      ensurePopover().innerHTML = `
        <div class="rh-date-popover-head">
          <button type="button" class="rh-date-nav" data-date-nav="prev" aria-label="Previous month">&lsaquo;</button>
          <strong>${monthLabel}</strong>
          <button type="button" class="rh-date-nav" data-date-nav="next" aria-label="Next month">&rsaquo;</button>
        </div>
        <div class="rh-date-weekdays" aria-hidden="true">
          <span>Su</span><span>Mo</span><span>Tu</span><span>We</span><span>Th</span><span>Fr</span><span>Sa</span>
        </div>
        <div class="rh-date-grid">
          ${days.join("")}
        </div>`;

      positionPopover();
    };

    const openDatePicker = (input) => {
      if (!input || input.disabled || input.readOnly && input.dataset.rhDatePickerBound !== "true") {
        return;
      }

      const selected = parseIsoDate(input.value) || parseIsoDate(input.min) || new Date();
      datePickerState.activeInput = input;
      datePickerState.activeMonth = new Date(selected.getFullYear(), selected.getMonth(), 1);
      ensurePopover().hidden = false;
      renderDatePicker();
    };

    dateInputs.forEach((input) => {
      if (input.dataset.rhDatePickerBound === "true") {
        return;
      }

      input.dataset.rhDatePickerBound = "true";
      input.type = "text";
      input.inputMode = "none";
      input.autocomplete = "off";
      input.readOnly = true;
      input.classList.add("rh-date-input");
      input.placeholder = input.placeholder || "Select date";

      const wrapper = document.createElement("div");
      wrapper.className = "rh-date-field";
      input.parentNode.insertBefore(wrapper, input);
      wrapper.appendChild(input);

      const icon = document.createElement("span");
      icon.className = "rh-date-icon";
      icon.setAttribute("aria-hidden", "true");
      wrapper.prepend(icon);

      const chevron = document.createElement("span");
      chevron.className = "rh-date-chevron";
      chevron.setAttribute("aria-hidden", "true");
      wrapper.appendChild(chevron);

      input.addEventListener("focus", () => openDatePicker(input));
      input.addEventListener("click", () => openDatePicker(input));
      input.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
          closeDatePicker();
          return;
        }

        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          openDatePicker(input);
        }
      });
    });

    if (document.body.dataset.rhDatePickerGlobalBound !== "true") {
      document.body.dataset.rhDatePickerGlobalBound = "true";

      document.addEventListener("click", (event) => {
        const target = event.target;
        if (target.closest?.(".rh-date-field")) {
          return;
        }

        if (target.closest?.("#rhDatePickerPopover")) {
          return;
        }

        closeDatePicker();
      });

      document.addEventListener("click", (event) => {
        const navButton = event.target.closest?.("[data-date-nav]");
        if (navButton && datePickerState.activeMonth) {
          datePickerState.activeMonth = new Date(
            datePickerState.activeMonth.getFullYear(),
            datePickerState.activeMonth.getMonth() + (navButton.dataset.dateNav === "next" ? 1 : -1),
            1
          );
          renderDatePicker();
          return;
        }

        const dayButton = event.target.closest?.("[data-date-value]");
        if (!dayButton || !datePickerState.activeInput) {
          return;
        }

        datePickerState.activeInput.value = dayButton.dataset.dateValue;
        datePickerState.activeInput.dispatchEvent(new Event("input", { bubbles: true }));
        datePickerState.activeInput.dispatchEvent(new Event("change", { bubbles: true }));
        closeDatePicker();
      });

      window.addEventListener("resize", positionPopover);
      window.addEventListener("scroll", positionPopover, true);
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
      const submitSectionRefresh = () => {
        const propertyRoot = form.closest("[data-property-overview-root='true']");
        if (propertyRoot && form.dataset.sectionSearch) {
          const payload = Object.fromEntries(new FormData(form).entries());
          refreshPropertyOverview(propertyRoot, {
            sectionName: form.dataset.sectionSearch,
            overrides: payload
          }).catch(() => {
            showFeedbackModal("error", "Unable to refresh this section right now.", getDialogOptions(propertyRoot));
          });
          return true;
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
          return true;
        }

        return false;
      };

      form.addEventListener("submit", (event) => {
        if (submitSectionRefresh()) {
          event.preventDefault();
        }
      });

      let timer = null;
      input.addEventListener("input", () => {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => {
          if (submitSectionRefresh()) {
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

      if (file.size > 4 * 1024 * 1024) {
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

  const initPropertyImageLightbox = () => {
    const modal = document.getElementById("propertyImageLightbox");
    if (!modal || modal.dataset.bound === "true") {
      return;
    }

    const preview = modal.querySelector("#propertyImageLightboxPreview");
    const images = Array.from(modal.querySelectorAll("[data-property-lightbox-image]"))
      .map((item) => ({
        url: item.dataset.imageUrl || "",
        name: item.dataset.imageName || "Property image"
      }))
      .filter((item) => item.url);

    if (!preview || !images.length) {
      return;
    }

    let activeIndex = Math.max(0, images.findIndex((item) => item.url === preview.getAttribute("src")));
    const updateImage = (nextIndex) => {
      activeIndex = (nextIndex + images.length) % images.length;
      const image = images[activeIndex];
      preview.src = image.url;
      preview.alt = image.name;
    };

    modal.querySelector("[data-property-lightbox-nav='prev']")?.addEventListener("click", () => updateImage(activeIndex - 1));
    modal.querySelector("[data-property-lightbox-nav='next']")?.addEventListener("click", () => updateImage(activeIndex + 1));
    updateImage(activeIndex);
    modal.dataset.bound = "true";
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

      propertySettingsForm.querySelectorAll("select[name]").forEach((field) => {
        field.disabled = !isEditing;
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
    initDatePickers(root);
    initCountrySelectors(root);
    initPropertyImageUpload(root);
    initPropertyImageLightbox();
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
    const lightboxPrev = document.querySelector("[data-lightbox-nav='prev']");
    const lightboxNext = document.querySelector("[data-lightbox-nav='next']");
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
    if (lightboxPrev) {
      lightboxPrev.onclick = () => updateSlide(activeIndex - 1);
    }

    if (lightboxNext) {
      lightboxNext.onclick = () => updateSlide(activeIndex + 1);
    }

    manageOpen?.addEventListener("click", updateManageModal);
    gallery.querySelectorAll("[data-image-lightbox-trigger='true']").forEach((trigger) => {
      if (trigger.dataset.bound === "true") {
        return;
      }

      trigger.dataset.bound = "true";
      trigger.addEventListener("click", () => {
        const triggerSlide = trigger.closest("[data-gallery-slide]");
        const triggerIndex = triggerSlide ? slides.indexOf(triggerSlide) : activeIndex;
        updateSlide(triggerIndex >= 0 ? triggerIndex : activeIndex);
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
    initDatePickers(root);
    initApartmentGallery(root);
    initApartmentReminderEditor(root);
  };

  const initRentPeriodPickers = (root = document) => {
    root.querySelectorAll("[data-rent-period-picker]").forEach((picker) => {
      if (picker.dataset.bound === "true") {
        return;
      }

      const form = picker.closest("form");
      const toggleButton = picker.querySelector("[data-rent-picker-toggle]");
      const countInput = form?.querySelector("[data-rent-period-count]");
      const submitButton = form?.querySelector("[data-rent-submit]");
      const helpText = form?.querySelector("[data-rent-picker-help]");
      const options = Array.from(picker.querySelectorAll("[data-rent-period-option]"));

      if (!form || !toggleButton || !countInput || !submitButton || options.length === 0) {
        return;
      }

      const closePicker = () => {
        picker.classList.remove("is-open");
      };

      const updatePicker = () => {
        let selectedCount = 0;
        let lastSelectedOption = null;
        let gapFound = false;

        options.forEach((option, index) => {
          option.disabled = index > 0 && !options[index - 1].checked;

          if (option.disabled || gapFound) {
            option.checked = false;
          }

          if (option.checked) {
            selectedCount = index + 1;
            lastSelectedOption = option;
          } else {
            gapFound = true;
          }
        });

        countInput.value = selectedCount.toString();
        submitButton.disabled = selectedCount === 0;

        if (selectedCount === 0) {
          toggleButton.textContent = "Choose rent periods";
          if (helpText) {
            helpText.textContent = "Select the oldest period first. Each selected period unlocks the next one.";
          }
          return;
        }

        const periodLabel = lastSelectedOption?.dataset.label || "selected period";
        const total = lastSelectedOption?.dataset.total || "";
        toggleButton.textContent = `Pay ${selectedCount} period${selectedCount > 1 ? "s" : ""} through ${periodLabel}${total ? ` - ${total}` : ""}`;
        if (helpText) {
          helpText.textContent = "Rent periods are selected in order. Unselecting one period clears every period after it.";
        }
      };

      toggleButton.addEventListener("click", (event) => {
        event.stopPropagation();
        picker.classList.toggle("is-open");
      });

      options.forEach((option) => {
        option.addEventListener("change", updatePicker);
      });

      form.addEventListener("submit", (event) => {
        updatePicker();
        if (Number.parseInt(countInput.value, 10) <= 0) {
          event.preventDefault();
        }
      });

      document.addEventListener("click", (event) => {
        if (!picker.contains(event.target)) {
          closePicker();
        }
      });

      picker.dataset.bound = "true";
      updatePicker();
    });
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

  const setSidebarOpen = (isOpen) => {
    body.classList.toggle("sidebar-open", isOpen);
    toggle?.setAttribute("aria-expanded", isOpen ? "true" : "false");
  };

  const closeSidebar = () => setSidebarOpen(false);

  if (toggle) {
    toggle.addEventListener("click", () => {
      setSidebarOpen(!body.classList.contains("sidebar-open"));
    });
  }

  sidebarClose?.addEventListener("click", closeSidebar);
  sidebarScrim?.addEventListener("click", closeSidebar);

  sidebar?.querySelectorAll("a").forEach((link) => {
    link.addEventListener("click", () => {
      if (window.matchMedia("(max-width: 992px)").matches) {
        closeSidebar();
      }
    });
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && body.classList.contains("sidebar-open")) {
      closeSidebar();
    }
  });

  window.addEventListener("resize", () => {
    if (!window.matchMedia("(max-width: 992px)").matches) {
      closeSidebar();
    }
  });

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
  initCountrySelectors(document);
  initDatePickers(document);
  initRentPeriodPickers(document);
  initSessionKeepAlive();
})();


