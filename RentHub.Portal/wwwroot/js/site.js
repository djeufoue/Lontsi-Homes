(() => {
  const body = document.body;
  const toggle = document.getElementById("rhSidebarToggle");

  if (toggle) {
    toggle.addEventListener("click", () => {
      body.classList.toggle("sidebar-open");
    });
  }
})();
