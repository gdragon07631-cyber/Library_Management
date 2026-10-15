(() => {
    const form = document.getElementById('overdue-export');
    if (!form) return;
    const button = form.querySelector('button[type="submit"]');
    const message = document.getElementById('overdue-export-message');
    const minimumDays = document.getElementById('minimumOverdueDays');
    const cardType = document.getElementById('exportLibraryCardTypeId');
    const loadStatus = document.getElementById('export-card-types-status');
    const retry = document.getElementById('reload-export-card-types');
    let loaded = false;
    async function loadCardTypes() {
        loaded = false;
        button.disabled = true;
        cardType.disabled = true;
        retry.hidden = true;
        loadStatus.textContent = 'Đang tải loại thẻ…';
        try {
            const response = await fetch(cardType.dataset.source, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
            if (!response.ok) throw new Error();
            const types = await response.json();
            if (!Array.isArray(types) || types.some(t => !Number.isInteger(t.id) || t.id <= 0 || typeof t.name !== 'string')) throw new Error();
            const options = [{ id: '', name: 'Tất cả' }, ...types].map(type => {
                const option = document.createElement('option');
                option.value = String(type.id);
                option.textContent = type.name;
                return option;
            });
            cardType.replaceChildren(...options);
            cardType.value = '';
            loaded = true;
            cardType.disabled = false;
            button.disabled = false;
            loadStatus.textContent = '';
        } catch {
            loadStatus.textContent = 'Không thể tải danh sách loại thẻ. Vui lòng thử lại.';
            retry.hidden = false;
        }
    }
    retry.addEventListener('click', loadCardTypes);
    loadCardTypes();
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (button.disabled || !loaded) return;
        const threshold = minimumDays.value.trim();
        if (threshold !== '' && (!/^[0-9]+$/.test(threshold) || Number(threshold) > 2147483647)) {
            message.className = 'mt-2 text-danger';
            message.textContent = 'Số ngày trễ tối thiểu phải là số nguyên không âm từ 0 đến 2147483647';
            minimumDays.setAttribute('aria-invalid', 'true');
            minimumDays.focus();
            return;
        }
        minimumDays.removeAttribute('aria-invalid');
        // Snapshot the input before awaiting: later edits belong to the next request.
        const body = new FormData(form);
        if (threshold === '') body.delete('minimumOverdueDays');
        else body.set('minimumOverdueDays', threshold);
        if (cardType.value === '') body.delete('libraryCardTypeId');
        else body.set('libraryCardTypeId', cardType.value);
        button.disabled = true;
        button.textContent = 'Đang xuất…';
        message.textContent = '';
        try {
            const response = await fetch(form.action, {
                method: 'POST', body, credentials: 'same-origin',
                headers: { 'Accept': 'application/json', 'X-Requested-With': 'XMLHttpRequest' }
            });
            const type = response.headers.get('Content-Type') || '';
            if (!response.ok || !type.startsWith('application/vnd.openxmlformats-officedocument.spreadsheetml.sheet')) {
                let detail;
                if (type.includes('application/json')) detail = (await response.json()).message;
                throw new Error(detail || 'Không thể xuất Excel. Vui lòng tải lại trang và thử lại.');
            }
            const disposition = response.headers.get('Content-Disposition') || '';
            const utf8 = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
            const plain = /filename="?([^";]+)"?/i.exec(disposition);
            const name = utf8 ? decodeURIComponent(utf8[1]) : plain?.[1];
            if (!name) throw new Error('Phản hồi thiếu tên tệp Excel. Vui lòng thử lại.');
            const url = URL.createObjectURL(await response.blob());
            const link = document.createElement('a');
            link.href = url;
            link.download = name;
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
            message.className = 'mt-2 text-success';
            message.textContent = 'Tệp Excel đã được chuẩn bị và gửi tới trình duyệt.';
        } catch (error) {
            message.className = 'mt-2 text-danger';
            message.textContent = error.message || 'Không thể xuất Excel. Vui lòng thử lại.';
        } finally {
            button.disabled = !loaded;
            button.textContent = 'Xuất Excel';
        }
    });
})();
