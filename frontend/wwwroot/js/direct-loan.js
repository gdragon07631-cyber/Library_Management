(() => {
    const form = document.getElementById('confirmDirectLoan');
    const addForm = document.getElementById('addDirectLoanCopy');
    const card = document.getElementById('CardCode');
    const input = document.getElementById('CopyCode');
    const body = document.getElementById('selectedCopies');
    const message = document.getElementById('selectionMessage');
    const warning = document.getElementById('limitWarning');
    const confirm = form.querySelector('button[type="submit"]');
    const state = new DirectLoanSelectionState(Array.from(body.rows, row => ({
        rowId: row.dataset.rowId, copyCode: row.dataset.code, bookTitle: row.dataset.title,
        status: row.dataset.status, error: row.dataset.error || null, holdId: row.dataset.holdId || null
    })));
    const originalCard = form.elements.CardCode.value;
    const remaining = Number(form.dataset.remaining);
    let submitted = false;
    let uncertain = form.dataset.verifyOnly === 'true';
    let attempted = form.dataset.retrying === 'true';
    const verify = document.getElementById('verifyDirectLoan');
    const requestKey = form.elements.RequestId;
    const storageKey = 'directLoanPending';
    const locked = () => submitted || uncertain;
    function remember(phase) {
        sessionStorage.setItem(storageKey, JSON.stringify({ requestId: requestKey.value, cardCode: card.value.trim(), rows: state.rows, phase }));
    }
    function changed() {
        if (attempted) { requestKey.value = crypto.randomUUID(); attempted = false; sessionStorage.removeItem(storageKey); }
    }
    const readerValid = () => form.dataset.readerValid === 'true' && card.value.trim() === originalCard;
    const show = text => { message.textContent = text; message.hidden = !text; };
    function hidden(cell, name, value) {
        const element = document.createElement('input');
        element.type = 'hidden'; element.name = name; element.value = value; cell.append(element);
    }
    function button(cell, text, label, action) {
        const element = document.createElement('button');
        element.type = 'button'; element.className = 'btn btn-outline-secondary btn-sm me-2'; element.textContent = text;
        element.setAttribute('aria-label', label); element.disabled = locked();
        element.addEventListener('click', action); cell.append(element);
    }
    function render() {
        // Preserve edits when another row's asynchronous check finishes.
        const focused = document.activeElement;
        const focusId = focused?.dataset.editId;
        const selectionStart = focusId ? focused.selectionStart : null;
        const drafts = new Map(Array.from(body.querySelectorAll('[data-edit-id]'), element => [element.dataset.editId, element.value]));
        body.replaceChildren();
        state.rows.forEach((copy, index) => {
            const row = body.insertRow(); row.dataset.rowId = copy.rowId;
            row.insertCell().textContent = index + 1;
            const codeCell = row.insertCell();
            if (copy.editing) {
                const edit = document.createElement('input');
                edit.className = 'form-control'; edit.maxLength = 50; edit.value = drafts.get(copy.rowId) ?? copy.copyCode;
                edit.dataset.editId = copy.rowId; edit.setAttribute('aria-label', `Sửa mã vạch dòng ${index + 1}`);
                edit.addEventListener('keydown', event => {
                    if (event.key === 'Enter') { event.preventDefault(); recheck(copy.rowId, edit.value); }
                });
                codeCell.append(edit);
                button(codeCell, 'Kiểm tra lại', `Kiểm tra lại dòng ${index + 1}`, () => recheck(copy.rowId, edit.value));
            } else codeCell.textContent = copy.copyCode;
            row.insertCell().textContent = copy.bookTitle;
            row.insertCell().textContent = copy.status;
            const validation = row.insertCell();
            validation.textContent = copy.editing ? 'Đang sửa mã' : copy.checking ? 'Đang kiểm tra' : copy.error || 'Hợp lệ';
            if (copy.holdId && !copy.editing && !copy.checking) {
                const hold = document.createElement('div'); hold.className = 'small';
                hold.textContent = `Đơn đặt trước #${copy.holdId}`; validation.append(hold);
            }
            validation.className = copy.error ? 'text-danger' : copy.checking || copy.editing ? 'text-muted' : 'text-success';
            validation.setAttribute('aria-live', 'polite');
            const cell = row.insertCell();
            hidden(cell, 'CopyCodes', copy.copyCode); hidden(cell, 'RowIds', copy.rowId);
            if (!copy.editing && (copy.error || copy.checking)) button(cell, 'Sửa', `Sửa ${copy.copyCode}`, () => {
                changed(); state.edit(copy.rowId); render();
                Array.from(body.querySelectorAll('[data-edit-id]')).find(e => e.dataset.editId === copy.rowId)?.focus();
            });
            button(cell, 'Xóa', `Xóa ${copy.copyCode}`, () => { changed(); state.remove(copy.rowId); show(''); render(); input.focus(); });
        });
        if (focusId) {
            const edit = Array.from(body.querySelectorAll('[data-edit-id]')).find(e => e.dataset.editId === focusId);
            if (edit) { edit.focus(); edit.setSelectionRange(selectionStart, selectionStart); }
        }
        const counts = state.counts;
        for (const [id, value] of Object.entries({ selectedCount: counts.total, validCount: counts.valid,
            errorCount: counts.errors, pendingCount: counts.pending, remainingSelection: Math.max(0, remaining - counts.valid) }))
            document.getElementById(id).textContent = value;
        warning.hidden = counts.valid <= remaining;
        warning.textContent = `Có ${counts.valid} bản sao hợp lệ, nhưng chỉ còn được mượn thêm ${remaining} sách. Vui lòng xóa bớt bản sao.`;
        document.getElementById('rowWarning').hidden = counts.errors === 0 && counts.pending === 0;
        confirm.disabled = locked() || !state.canConfirm(remaining, readerValid());
        confirm.textContent = attempted ? 'Thử lại' : 'Xác nhận mượn';
        verify.hidden = !uncertain;
        verify.disabled = submitted;
        card.disabled = locked();
        card.form.querySelector('button').disabled = locked();
        input.disabled = locked() || !readerValid();
        addForm.querySelector('button').disabled = input.disabled;
    }
    async function lookup(row) {
        const { rowId, version, copyCode } = row;
        try {
            const url = new URL(addForm.dataset.lookupUrl, window.location.origin);
            url.searchParams.set('cardCode', originalCard); url.searchParams.set('copyCode', copyCode); url.searchParams.set('rowId', rowId);
            const response = await fetch(url, { headers: { Accept: 'application/json' } });
            if (response.redirected) throw new Error('Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.');
            const result = await response.json();
            if (!response.ok) throw new Error(result.error || 'Không thể kiểm tra mã vạch.');
            if (result.rowId !== rowId) throw new Error('Không thể xác định kết quả kiểm tra. Vui lòng thử lại.');
            state.apply(rowId, version, result);
        } catch (error) { state.apply(rowId, version, { error: error.message || 'Không thể kiểm tra mã vạch. Vui lòng thử lại.' }); }
        render();
    }
    function recheck(rowId, code) {
        if (locked()) return;
        changed();
        const row = state.check(rowId, code); render();
        if (row?.checking) void lookup(row);
    }
    card.addEventListener('input', () => { changed(); show(readerValid() ? '' : 'Vui lòng tra cứu lại mã thẻ trước khi tiếp tục.'); render(); });
    addForm.addEventListener('submit', event => {
        event.preventDefault();
        if (locked() || !readerValid()) return;
        const code = input.value.trim();
        if (!code) { show('Vui lòng nhập mã vạch bản sao.'); input.focus(); return; }
        if (state.duplicate(code)) { show('Mã vạch này đã có trong danh sách mượn.'); input.select(); return; }
        changed();
        const row = state.add(crypto.randomUUID(), code);
        if (!row) { show('Mã vạch này đã có trong danh sách mượn.'); input.select(); return; }
        show(''); input.value = ''; render(); input.focus(); void lookup(row);
    });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (locked() || !state.canConfirm(remaining, readerValid())) return;
        try { remember('pending'); } catch { show('Không thể lưu thông tin khôi phục trong trình duyệt. Vui lòng cho phép lưu trữ phiên trước khi xác nhận.'); return; }
        attempted = true; submitted = true; render();
        try {
            const response = await fetch(form.action, { method: 'POST', body: new FormData(form), headers: { Accept: 'application/json' } });
            if (!response.ok || response.redirected) throw new Error('Không nhận được kết quả xác nhận.');
            const result = await response.json();
            if (result.isSuccess) { uncertain = true; window.location.assign(result.url); return; }
            uncertain = Boolean(result.isUncertain || result.requestConflict);
            if (result.rows) state.rows = result.rows.map(row => ({ ...row, version: 0, checking: false, editing: false }));
            remember(uncertain ? 'pending' : 'retry');
            show(result.errorMessage);
        } catch { uncertain = true; show('Đang xác minh kết quả lượt mượn. Hãy kiểm tra kết quả trước khi thử lại.'); }
        finally { submitted = false; render(); }
    });
    async function verifyResult() {
        submitted = true; render();
        try {
            const url = new URL(form.dataset.statusUrl, location.origin); url.searchParams.set('requestId', requestKey.value);
            const response = await fetch(url, { headers: { Accept: 'application/json' } });
            if (!response.ok || response.redirected) throw new Error();
            const result = await response.json();
            if (result.isSuccess) { uncertain = true; window.location.assign(result.url); return; }
            uncertain = Boolean(result.isUncertain || result.requestConflict);
            show(result.errorMessage); remember(uncertain ? 'pending' : 'retry');
            if (!uncertain && card.value.trim() !== originalCard) {
                const destination = new URL(form.dataset.indexUrl, location.origin);
                destination.searchParams.set('cardCode', card.value.trim()); window.location.assign(destination); return;
            }
        } catch { uncertain = true; show('Chưa thể xác minh kết quả. Vui lòng kiểm tra lại, không tạo lượt mới.'); }
        finally { submitted = false; render(); }
    }
    verify.addEventListener('click', verifyResult);
    window.addEventListener('pageshow', () => {
        submitted = false;
        try {
            const saved = JSON.parse(sessionStorage.getItem(storageKey) || 'null');
            if (form.dataset.completed === 'true') {
                if (saved?.requestId === requestKey.value) sessionStorage.removeItem(storageKey);
                requestKey.value = crypto.randomUUID(); attempted = false;
            } else if (saved) {
                if (saved.phase === 'retry' && saved.cardCode !== originalCard) {
                    const url = new URL(form.dataset.indexUrl, location.origin); url.searchParams.set('cardCode', saved.cardCode);
                    window.location.assign(url); return;
                }
                requestKey.value = saved.requestId; card.value = saved.cardCode;
                state.rows = saved.rows; attempted = true; uncertain = saved.phase === 'pending';
                if (uncertain) void verifyResult();
            }
        } catch { uncertain = true; show('Không thể đọc thông tin lượt trước. Vui lòng kiểm tra kết quả.'); }
        render();
    });
    render();
    if (readerValid()) input.focus();
})();
