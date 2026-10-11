(() => {
    const form = document.getElementById('confirmDirectLoan');
    const addForm = document.getElementById('addDirectLoanCopy');
    const card = document.getElementById('CardCode');
    const input = document.getElementById('CopyCode');
    const body = document.getElementById('selectedCopies');
    const message = document.getElementById('selectionMessage');
    const warning = document.getElementById('limitWarning');
    const confirm = form.querySelector('button[type="submit"]');
    const selected = Array.from(body.rows, row => ({ copyCode: row.dataset.code, bookTitle: row.dataset.title, status: row.dataset.status }));
    const originalCard = form.elements.CardCode.value;
    const remaining = Number(form.dataset.remaining);
    let loading = false;
    let submitted = false;
    const readerValid = () => form.dataset.readerValid === 'true' && card.value.trim() === originalCard;
    const show = text => { message.textContent = text; message.hidden = !text; };
    function render() {
        body.replaceChildren();
        selected.forEach((copy, index) => {
            const row = body.insertRow();
            for (const text of [index + 1, copy.copyCode, copy.bookTitle, copy.status]) row.insertCell().textContent = text;
            const cell = row.insertCell();
            const hidden = document.createElement('input');
            hidden.type = 'hidden'; hidden.name = 'CopyCodes'; hidden.value = copy.copyCode;
            const remove = document.createElement('button');
            remove.type = 'button'; remove.className = 'btn btn-outline-danger btn-sm'; remove.textContent = 'Xóa';
            remove.setAttribute('aria-label', `Xóa ${copy.copyCode}`);
            remove.disabled = submitted;
            remove.addEventListener('click', () => { selected.splice(index, 1); show(''); render(); input.focus(); });
            cell.append(hidden, remove);
        });
        document.getElementById('selectedCount').textContent = selected.length;
        document.getElementById('remainingSelection').textContent = Math.max(0, remaining - selected.length);
        warning.hidden = selected.length <= remaining;
        warning.textContent = `Đã chọn ${selected.length} bản sao, nhưng chỉ còn được mượn thêm ${remaining} sách. Vui lòng xóa bớt bản sao.`;
        confirm.disabled = submitted || loading || !readerValid() || selected.length === 0 || selected.length > remaining;
        addForm.querySelector('button').disabled = submitted || loading || !readerValid();
        input.disabled = submitted || loading || !readerValid();
    }
    card.addEventListener('input', () => { show(readerValid() ? '' : 'Vui lòng tra cứu lại mã thẻ trước khi tiếp tục.'); render(); });
    addForm.addEventListener('submit', async event => {
        event.preventDefault();
        if (loading || submitted || !readerValid()) return;
        const code = input.value.trim();
        if (!code) { show('Vui lòng nhập mã vạch bản sao.'); input.focus(); return; }
        if (selected.some(c => c.copyCode.toUpperCase() === code.toUpperCase())) {
            show('Mã vạch này đã có trong danh sách mượn.'); input.select(); return;
        }
        loading = true; show(''); render();
        try {
            const url = new URL(addForm.dataset.lookupUrl, window.location.origin);
            url.searchParams.set('cardCode', originalCard); url.searchParams.set('copyCode', code);
            const response = await fetch(url, { headers: { Accept: 'application/json' } });
            if (response.redirected) throw new Error('Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.');
            const result = await response.json();
            if (!response.ok) throw new Error(result.error || 'Không thể tra cứu bản sao.');
            if (!readerValid()) return;
            if (selected.some(c => c.copyCode.toUpperCase() === result.copyCode.toUpperCase()))
                throw new Error('Mã vạch này đã có trong danh sách mượn.');
            selected.push(result); input.value = '';
        } catch (error) { show(error.message || 'Không thể tra cứu bản sao. Vui lòng thử lại.'); }
        finally { loading = false; render(); input.focus(); }
    });
    form.addEventListener('submit', event => {
        if (confirm.disabled) { event.preventDefault(); return; }
        submitted = true; render();
    });
    window.addEventListener('pageshow', () => { submitted = false; render(); });
    render();
    if (readerValid()) input.focus();
})();
