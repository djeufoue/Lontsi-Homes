document.querySelectorAll('[data-payment-correction-form]').forEach(function (form) {
    if (form.dataset.correctionReady) return;
    form.dataset.correctionReady = 'true';
    const boxes = Array.from(form.querySelectorAll('[data-correction-period]'));
    const reason = form.querySelector('[name="Reason"]');
    const button = form.querySelector('[data-correction-submit]');
    const summary = form.querySelector('[data-correction-summary]');
    function refresh() {
        let prefix = true;
        boxes.forEach(function (box) {
            box.disabled = !prefix;
            if (!prefix) box.checked = false;
            prefix = prefix && box.checked;
        });
        const selected = boxes.filter(box => box.checked && !box.disabled);
        const amount = selected.reduce((sum, box) => sum + Number(box.dataset.amount), 0);
        summary.textContent = summary.dataset.summaryTemplate.replace('{0}', selected.length)
            .replace('{1}', amount.toLocaleString(document.documentElement.lang || 'fr'));
        button.disabled = selected.length === 0 || reason.value.trim().length < 5;
        // Confirmation helper renders text, not HTML. The preview includes the exact
        // selected periods and total, plus the persistent consequences warning.
        form.dataset.confirmMessage = summary.textContent + '\n' + selected.map(box => box.dataset.label).join('\n') +
            '\n\n' + form.dataset.correctionWarning + '\n\n' + reason.value.trim();
    }
    boxes.forEach(box => box.addEventListener('change', refresh));
    reason.addEventListener('input', refresh);
    form.addEventListener('rent-batch:refresh', refresh);
    form.addEventListener('submit', function (event) {
        refresh();
        if (button.disabled || !form.checkValidity()) {
            event.preventDefault();
            event.stopPropagation();
        }
    });
    refresh();
});
