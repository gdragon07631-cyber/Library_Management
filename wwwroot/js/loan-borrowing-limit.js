(() => {
  const readerSelect = document.getElementById('reader-account-id');
  const statusElement = document.getElementById('reader-borrowing-status');
  const submitButton = document.getElementById('create-loan-button');
  const form = document.getElementById('create-loan-form');
  const overrideButton = document.getElementById('override-loan-button');
  const overrideDialog = document.getElementById('loan-override-dialog');
  const overrideReader = document.getElementById('loan-override-reader');
  const overrideReasons = document.getElementById('loan-override-reasons');
  const overrideReasonInput = document.getElementById('loan-override-reason-input');
  const overrideError = document.getElementById('loan-override-error');
  const overrideEnabled = document.getElementById('loan-override-enabled');
  const overrideReason = document.getElementById('loan-override-reason');
  const overrideRequestId = document.getElementById('loan-override-request-id');
  if (!readerSelect || !statusElement || !submitButton) return;

  let activeRequest;
  let requestNumber = 0;
  let currentStatus;

  const newRequestId = () => crypto.randomUUID();
  const resetOverride = () => {
    if (overrideEnabled) overrideEnabled.value = 'false';
    if (overrideReason) overrideReason.value = '';
    if (overrideReasonInput) overrideReasonInput.value = '';
  };

  form?.addEventListener('submit', (event) => {
    if (currentStatus?.isBlocked &&
        !(currentStatus.canOverride && overrideEnabled?.value === 'true')) {
      event.preventDefault();
      statusElement.focus();
    }
  });

  overrideButton?.addEventListener('click', () => {
    if (!currentStatus?.isBlocked || !currentStatus.canOverride || !overrideDialog) return;
    overrideReader.textContent = `Bạn đọc: ${currentStatus.readerCode} — ${currentStatus.readerName}`;
    overrideReasons.textContent = currentStatus.blockingReasons
      .map((reason, index) => `${index + 1}. ${reason.message}`)
      .join('\n');
    overrideReasonInput.value = '';
    overrideError.textContent = '';
    overrideDialog.showModal();
    overrideReasonInput.focus();
  });

  document.getElementById('loan-override-cancel')?.addEventListener('click', () => {
    overrideDialog?.close();
  });

  document.getElementById('loan-override-confirm')?.addEventListener('click', () => {
    const reason = overrideReasonInput?.value.trim() || '';
    if (!reason) {
      overrideError.textContent = 'Vui lòng nhập lý do bỏ qua.';
      overrideReasonInput?.focus();
      return;
    }
    if (!currentStatus?.isBlocked || !currentStatus.canOverride) {
      overrideError.textContent = 'Quyền bỏ qua hoặc trạng thái chặn đã thay đổi. Vui lòng kiểm tra lại.';
      return;
    }
    overrideEnabled.value = 'true';
    overrideReason.value = reason;
    overrideDialog.close();
    form.requestSubmit();
  });

  readerSelect.addEventListener('change', async () => {
    const currentRequest = ++requestNumber;
    activeRequest?.abort();
    activeRequest = undefined;
    statusElement.hidden = true;
    statusElement.textContent = '';
    statusElement.className = 'alert mb-0';
    submitButton.disabled = true;
    currentStatus = undefined;
    if (overrideButton) overrideButton.hidden = true;
    resetOverride();
    if (overrideRequestId) overrideRequestId.value = newRequestId();

    if (!readerSelect.value) return;

    activeRequest = new AbortController();
    statusElement.hidden = false;
    statusElement.classList.add('alert-info');
    statusElement.textContent = 'Đang kiểm tra điều kiện mượn...';

    try {
      const response = await fetch(
        `/api/loans/readers/${encodeURIComponent(readerSelect.value)}/borrowing-status`,
        { headers: { Accept: 'application/json' }, signal: activeRequest.signal });
      const data = await response.json();
      if (!response.ok) throw new Error(data.message || 'Không thể kiểm tra hạn mức của bạn đọc.');
      if (currentRequest !== requestNumber) return;
      currentStatus = data;

      statusElement.classList.remove('alert-info');
      const cardExpiry = data.cardExpiresOn
        ? data.cardExpiresOn.split('-').reverse().join('/')
        : 'Chưa có thẻ';
      const summary =
        `Mã bạn đọc: ${data.readerCode} — ${data.readerName}; loại thẻ: ${data.cardTypeName}. ` +
        `Trạng thái thẻ: ${data.cardStatus || 'Chưa có thẻ'}; ngày hết hạn: ${cardExpiry}. ` +
        `Sách quá hạn chưa trả: ${data.overdueLoanCount}. ` +
        `Đang mượn ${data.borrowedCount}/${data.borrowingLimit} cuốn; còn có thể mượn ${data.remaining} cuốn.\n` +
        `Tổng nợ chưa thanh toán: ${data.outstandingBalanceFormatted} VND.`;
      statusElement.style.whiteSpace = 'pre-line';
      statusElement.textContent = summary;
      if (data.isBlocked) {
        statusElement.classList.add('alert-danger');
        statusElement.textContent += `\n${data.blockingReasons
          .map((reason, index) => `${index + 1}. ${reason.message}`)
          .join('\n')}`;
        if (overrideButton) overrideButton.hidden = !data.canOverride;
      } else {
        statusElement.classList.add('alert-success');
        submitButton.disabled = false;
      }
    } catch (error) {
      if (error.name === 'AbortError' || currentRequest !== requestNumber) return;
      currentStatus = undefined;
      if (overrideButton) overrideButton.hidden = true;
      statusElement.classList.remove('alert-info');
      statusElement.classList.add('alert-danger');
      statusElement.textContent = error.message;
    }
  });
})();
