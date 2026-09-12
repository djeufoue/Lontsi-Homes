const fs = require("node:fs");
const vm = require("node:vm");
const assert = require("node:assert/strict");
const source = fs.readFileSync("RentHub.Portal/wwwroot/js/site.js", "utf8");
const start = source.indexOf("  if (confirmationModalEl && confirmationProceedEl) {");
const end = source.indexOf('\n  document.addEventListener("submit"', start);
assert(start >= 0 && end > start);
const element = () => ({
  listeners: {}, dataset: {}, textContent: "",
  classList: { toggle() {}, add() {}, remove() {} },
  setAttribute() {},
  addEventListener(name, handler) { this.listeners[name] = handler; }
});
const modal = element(), proceed = element(), warning = element(), message = element();
const warningText = element();
warningText.textContent = "Existing payment warning";
warning.querySelector = () => warningText;
const doc = element();
let opened = 0, submitted = 0;
const context = {
  confirmationModalEl: modal, confirmationProceedEl: proceed,
  confirmationWarningEl: warning, confirmationMessageEl: message,
  confirmationTitleEl: element(), confirmationDurationEl: element(),
  confirmationDurationSelectEl: null, document: doc,
  t: text => text, Event: class {},
  bootstrap: { Modal: class {
    show() { opened++; }
    hide() { modal.listeners["hidden.bs.modal"](); }
  } }
};
vm.runInNewContext(source.slice(start, end), context);
function form(data) {
  const result = {
    dataset: data, querySelectorAll: () => [], dispatchEvent() {},
    requestSubmit() {
      const event = { target: { closest: () => result }, prevented: false,
        preventDefault() { this.prevented = true; } };
      doc.listeners.submit(event);
      if (!event.prevented) submitted++;
    }
  };
  return result;
}
const whatsapp = form({
  confirmWarning: "true", confirmWarningMessage: "Withdraw WhatsApp consent",
  confirmMessage: "Emails remain unchanged", confirmProceed: "Yes, disable"
});
whatsapp.requestSubmit();
assert.equal(opened, 1);
assert.equal(submitted, 0);
assert.equal(warningText.textContent, "Withdraw WhatsApp consent");
assert.equal(message.textContent, "Emails remain unchanged");
modal.listeners["hidden.bs.modal"]();
proceed.listeners.click();
assert.equal(submitted, 0, "cancel or close must not submit");
whatsapp.requestSubmit();
proceed.listeners.click();
proceed.listeners.click();
assert.equal(submitted, 1, "confirmation submits exactly once, including synchronous close");
form({ confirmWarning: "true" }).requestSubmit();
assert.equal(warningText.textContent, "Existing payment warning", "payment warning is restored");
console.log("PASS confirmation: no premature submission; cancellation; one confirmed submission; existing payment warning preserved.");
