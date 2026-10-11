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
        status: row.dataset.status, error: row.dataset.error || null
    })));
    const originalCard = form.elements.CardCode.value;
    const remaining = Number(form.dataset.remaining);
    let submitted = false;
    const readerValid = () => form.dataset.readerValid === 'true' && card.value.trim() === originalCard;
    const show = text => { message.textContent = text; message.hidden = !text; };
    function hidden(cell, name, value) {
        const element = document.createElement('input');
        element.type = 'hidden'; element.name = name; element.value = value; cell.append(element);
    }
    function button(cell, text, label, action) {
        const element = document.createElement('button');
        element.type = 'button'; element.className = 'btn btn-outline-secondary btn-sm me-2'; element.textContent = text;
        element.setAttribute('aria-label', label); element.disabled = submitted;
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
            validation.className = copy.error ? 'text-danger' : copy.checking || copy.editing ? 'text-muted' : 'text-success';
            validation.setAttribute('aria-live', 'polite');
            const cell = row.insertCell();
            hidden(cell, 'CopyCodes', copy.copyCode); hidden(cell, 'RowIds', copy.rowId);
            if (!copy.editing && (copy.error || copy.checking)) button(cell, 'Sửa', `Sửa ${copy.copyCode}`, () => {
                state.edit(copy.rowId); render();
                Array.from(body.querySelectorAll('[data-edit-id]')).find(e => e.dataset.editId === copy.rowId)?.focus();
            });
            button(cell, 'Xóa', `Xóa ${copy.copyCode}`, () => { state.remove(copy.rowId); show(''); render(); input.focus(); });
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
        confirm.disabled = submitted || !state.canConfirm(remaining, readerValid());
        input.disabled = submitted || !readerValid();
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
        if (submitted) return;
        const row = state.check(rowId, code); render();
        if (row?.checking) void lookup(row);
    }
    card.addEventListener('input', () => { show(readerValid() ? '' : 'Vui lòng tra cứu lại mã thẻ trước khi tiếp tục.'); render(); });
    addForm.addEventListener('submit', event => {
        event.preventDefault();
        if (submitted || !readerValid()) return;
        const code = input.value.trim();
        if (!code) { show('Vui lòng nhập mã vạch bản sao.'); input.focus(); return; }
        const row = state.add(crypto.randomUUID(), code);
        if (!row) { show('Mã vạch này đã có trong danh sách mượn.'); input.select(); return; }
        show(''); input.value = ''; render(); input.focus(); void lookup(row);
    });
    form.addEventListener('submit', event => {
        if (submitted || !state.canConfirm(remaining, readerValid())) { event.preventDefault(); return; }
        submitted = true; render();
    });
    window.addEventListener('pageshow', () => { submitted = false; render(); });
    render();
    if (readerValid()) input.focus();
})();
