(() => {
    const form = document.getElementById('fee-payment-form');
    if (!form) return;
    let submitting = false;
    form.addEventListener('submit', event => {
        if (submitting) { event.preventDefault(); return; }
        if (!form.reportValidity()) { event.preventDefault(); return; }
        const amount = Number(form.querySelector('[name="Amount"]').value);
        if (!window.confirm(`Xác nhận đã nhận ${amount.toLocaleString('vi-VN')} đ từ bạn đọc?`)) {
            event.preventDefault(); return;
        }
        submitting = true;
        form.querySelector('button[type="submit"]').disabled = true;
    });
    window.addEventListener('pageshow', () => {
        submitting = false;
        form.querySelector('button[type="submit"]').disabled = false;
    });
})();
