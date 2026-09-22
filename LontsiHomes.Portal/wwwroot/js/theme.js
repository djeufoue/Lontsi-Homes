(() => {
  "use strict";

  const t = (key) => window.rhI18n?.[key] || key;

  const storageKey = "lontsihomes-theme";
  const supportedPreferences = new Set(["system", "dark", "light"]);
  const root = document.documentElement;
  const systemTheme = window.matchMedia("(prefers-color-scheme: dark)");
  let currentPreference = "dark";

  const getSavedTheme = () => {
    try {
      const savedTheme = window.localStorage.getItem(storageKey);
      return supportedPreferences.has(savedTheme) ? savedTheme : "dark";
    } catch {
      return "dark";
    }
  };

  const resolveEffectiveTheme = (preference) => {
    if (preference === "system") {
      return systemTheme.matches ? "dark" : "light";
    }

    return preference;
  };

  const getPreferenceLabel = (preference) => {
    if (preference === "dark") {
      return t("Dark");
    }

    if (preference === "light") {
      return t("Light");
    }

    return t("Device");
  };

  const updateThemeMenu = () => {
    document.querySelectorAll("[data-theme-current-label]").forEach((label) => {
      label.textContent = getPreferenceLabel(currentPreference);
    });

    document.querySelectorAll("[data-theme-option]").forEach((option) => {
      const isSelected = option.dataset.themeOption === currentPreference;
      option.classList.toggle("is-selected", isSelected);
      option.setAttribute("aria-checked", isSelected ? "true" : "false");
      option.querySelector("[data-theme-check]")?.toggleAttribute("hidden", !isSelected);
    });
  };

  const applyTheme = (preference, options = {}) => {
    const nextPreference = supportedPreferences.has(preference) ? preference : "dark";
    const shouldPersist = options.persist !== false;
    const effectiveTheme = resolveEffectiveTheme(nextPreference);

    currentPreference = nextPreference;
    root.setAttribute("data-bs-theme", effectiveTheme);
    root.setAttribute("data-rh-theme-preference", nextPreference);
    root.style.colorScheme = effectiveTheme;

    if (shouldPersist) {
      try {
        window.localStorage.setItem(storageKey, nextPreference);
      } catch {
        // Theme selection still works for this page when storage is unavailable.
      }
    }

    updateThemeMenu();
    document.dispatchEvent(new CustomEvent("lontsihomes:theme-changed", {
      detail: { preference: nextPreference, effectiveTheme }
    }));
  };

  const showAccountPanel = (panelToShow, panelToHide, focusTarget) => {
    if (!panelToShow || !panelToHide) {
      return;
    }

    panelToHide.hidden = true;
    panelToShow.hidden = false;
    window.requestAnimationFrame(() => focusTarget?.focus());
  };

  const initAppearanceMenu = () => {
    const dropdown = document.querySelector("[data-account-dropdown]");
    const dropdownContainer = dropdown?.closest(".dropdown");
    const accountPanel = dropdown?.querySelector("[data-account-menu-panel]");
    const appearancePanel = dropdown?.querySelector("[data-appearance-menu-panel]");
    const openButton = dropdown?.querySelector("[data-appearance-open]");
    const backButton = dropdown?.querySelector("[data-appearance-back]");

    if (!dropdown || !accountPanel || !appearancePanel || !openButton || !backButton) {
      updateThemeMenu();
      return;
    }

    const showMainMenu = (restoreFocus = false) => {
      showAccountPanel(accountPanel, appearancePanel, restoreFocus ? openButton : null);
      openButton.setAttribute("aria-expanded", "false");
    };

    openButton.addEventListener("click", () => {
      showAccountPanel(appearancePanel, accountPanel, backButton);
      openButton.setAttribute("aria-expanded", "true");
    });

    backButton.addEventListener("click", () => showMainMenu(true));

    dropdownContainer?.addEventListener("hidden.bs.dropdown", () => showMainMenu(false));
    updateThemeMenu();
  };

  const initThemeOptions = () => {
    document.querySelectorAll("[data-theme-option]").forEach((option) => {
      if (option.dataset.themeBound === "true") {
        return;
      }

      option.dataset.themeBound = "true";
      option.addEventListener("click", () => applyTheme(option.dataset.themeOption));
    });

    updateThemeMenu();
  };

  const initThemeControls = () => {
    initThemeOptions();
    initAppearanceMenu();
  };

  currentPreference = getSavedTheme();
  applyTheme(currentPreference, { persist: false });

  const handleSystemThemeChange = () => {
    if (currentPreference === "system") {
      applyTheme("system", { persist: false });
    }
  };

  if (typeof systemTheme.addEventListener === "function") {
    systemTheme.addEventListener("change", handleSystemThemeChange);
  } else if (typeof systemTheme.addListener === "function") {
    systemTheme.addListener(handleSystemThemeChange);
  }

  window.LontsiHomesTheme = {
    applyTheme,
    getPreference: () => currentPreference,
    getEffectiveTheme: () => resolveEffectiveTheme(currentPreference)
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initThemeControls, { once: true });
  } else {
    initThemeControls();
  }
})();
