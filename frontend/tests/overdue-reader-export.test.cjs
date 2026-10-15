const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

function page(fetch, value = '', load = async () => ({ ok: true, json: async () => [{ id: 1, name: 'Thẻ sinh viên' }, { id: 2, name: 'Thẻ khác' }] })) {
    let submit;
    const button = { disabled: false, textContent: 'Xuất Excel' };
    const message = {};
    const input = { value, setAttribute() {}, removeAttribute() {}, focus() {} };
    const cardType = { value: '', dataset: { source: '/api/overdue-readers/card-types' }, replaceChildren(...options) { this.options = options; } };
    const loadStatus = {};
    let reload;
    const retry = { addEventListener: (_, fn) => { reload = fn; } };
    const downloads = [];
    const form = { action: '/api/overdue-readers/export', querySelector: () => button,
        addEventListener: (_, fn) => { submit = fn; } };
    const context = { fetch: (url, options) => url === cardType.dataset.source ? load() : fetch(url, options), FormData: class extends Map { constructor() { super(); } }, setTimeout: fn => fn(),
        URL: { createObjectURL: () => 'blob:excel', revokeObjectURL() {} },
        document: { getElementById: id => ({ 'overdue-export': form, minimumOverdueDays: input,
            exportLibraryCardTypeId: cardType, 'export-card-types-status': loadStatus,
            'reload-export-card-types': retry, 'overdue-export-message': message })[id],
            body: { appendChild() {} }, createElement: () => ({ click() { downloads.push(this.download); }, remove() {} }) } };
    vm.runInNewContext(fs.readFileSync(require.resolve('../wwwroot/js/overdue-reader-export.js'), 'utf8'), context);
    return { button, message, input, cardType, loadStatus, retry, reload: () => reload(),
        ready: new Promise(resolve => setImmediate(resolve)), downloads, submit: () => submit({ preventDefault() {} }) };
}

test('disables duplicate exports and downloads using server filename', async () => {
    let resolve;
    let calls = 0;
    const p = page(() => { calls++; return new Promise(r => { resolve = r; }); }, '7');
    await p.ready;
    const pending = p.submit();
    assert.equal(p.button.disabled, true);
    assert.equal(p.button.textContent, 'Đang xuất…');
    await p.submit();
    assert.equal(calls, 1);
    resolve({ ok: true, headers: { get: key => key === 'Content-Type'
        ? 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
        : "attachment; filename*=UTF-8''Ban-doc-qua-han-no-phi_15-10-2026.xlsx" }, blob: async () => ({}) });
    await pending;
    assert.deepEqual(p.downloads, ['Ban-doc-qua-han-no-phi_15-10-2026.xlsx']);
    assert.equal(p.button.disabled, false);
    assert.equal(p.input.value, '7');
});

for (const text of ['Không có dữ liệu phù hợp với điều kiện đã chọn', 'Không thể lưu nhật ký xuất. Vui lòng thử lại.', 'Vui lòng đăng nhập bằng tài khoản nhân sự.']) {
    test('shows JSON error without downloading: ' + text, async () => {
        const p = page(async () => ({ ok: false, headers: { get: () => 'application/json' }, json: async () => ({ message: text }) }), '7');
        await p.ready;
        p.cardType.value = '1';
        await p.submit();
        assert.equal(p.message.textContent, text);
        assert.equal(p.button.disabled, false);
        assert.equal(p.button.textContent, 'Xuất Excel');
        assert.deepEqual(p.downloads, []);
        assert.equal(p.input.value, '7');
        assert.equal(p.cardType.value, '1');
    });
}
test('network failure restores button', async () => {
    const p = page(async () => { throw new Error('Mất kết nối'); });
    await p.ready;
    await p.submit();
    assert.equal(p.button.disabled, false);
    assert.equal(p.message.textContent, 'Mất kết nối');
    assert.deepEqual(p.downloads, []);
});

for (const value of ['-1', '7.5', '7.0', 'abc', '2147483648', '999999999999999999999999', '1e2', '+7', '7,5']) {
    test('rejects invalid threshold without sending: ' + value, async () => {
        let calls = 0;
        const p = page(async () => { calls++; }, value);
        await p.ready;
        await p.submit();
        assert.equal(calls, 0);
        assert.match(p.message.textContent, /phải là số nguyên không âm/);
        assert.equal(p.input.value, value);
        assert.equal(p.button.disabled, false);
    });
}

for (const [value, expected] of [['', undefined], ['  \t', undefined], ['0', '0'], [' 7 ', '7'], ['2147483647', '2147483647']]) {
    test('snapshots threshold and preserves input: ' + JSON.stringify(value), async () => {
        let body;
        let complete;
        const p = page((url, options) => {
            body = options.body;
            return new Promise(resolve => { complete = resolve; });
        }, value);
        await p.ready;
        p.cardType.value = '1';
        const pending = p.submit();
        assert.equal(body.get('minimumOverdueDays'), expected);
        assert.equal(body.get('libraryCardTypeId'), '1');
        p.input.value = '99';
        p.cardType.value = '2';
        assert.equal(body.get('minimumOverdueDays'), expected);
        assert.equal(body.get('libraryCardTypeId'), '1');
        complete({ ok: true, headers: { get: key => key === 'Content-Type'
            ? 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
            : 'attachment; filename="export.xlsx"' }, blob: async () => ({}) });
        await pending;
        assert.equal(p.input.value, '99');
        assert.equal(p.cardType.value, '2');
        assert.equal(p.button.disabled, false);
        assert.deepEqual(p.downloads, ['export.xlsx']);
    });
}

test('loads backend options with All default and submits no card condition for All', async () => {
    let body;
    const p = page(async (_, options) => {
        body = options.body;
        return { ok: false, headers: { get: () => 'application/json' }, json: async () => ({ message: 'Không có dữ liệu phù hợp với điều kiện đã chọn' }) };
    });
    assert.equal(p.button.disabled, true);
    await p.ready;
    assert.equal(p.button.disabled, false);
    assert.equal(p.cardType.value, '');
    assert.deepEqual(p.cardType.options.map(o => o.value), ['', '1', '2']);
    assert.equal(p.cardType.options[1].textContent, 'Thẻ sinh viên');
    await p.submit();
    assert.equal(body.has('libraryCardTypeId'), false);
    assert.equal(p.cardType.value, '');
});

test('failed option load blocks export and allows explicit retry', async () => {
    let loads = 0;
    let exports = 0;
    const p = page(async () => { exports++; }, '', async () => {
        loads++;
        return loads === 1 ? { ok: false } : { ok: true, json: async () => [{ id: 9, name: 'Loại thực tế' }] };
    });
    await p.ready;
    assert.match(p.loadStatus.textContent, /Không thể tải/);
    assert.equal(p.retry.hidden, false);
    assert.equal(p.button.disabled, true);
    await p.submit();
    assert.equal(exports, 0);
    await p.reload();
    assert.equal(p.retry.hidden, true);
    assert.equal(p.button.disabled, false);
    assert.equal(p.cardType.options[1].value, '9');
});

test('no active types is a successfully loaded All-only list', async () => {
    const p = page(async () => {}, '', async () => ({ ok: true, json: async () => [] }));
    await p.ready;
    assert.equal(p.button.disabled, false);
    assert.equal(p.cardType.options.length, 1);
    assert.equal(p.cardType.options[0].value, '');
});

test('deactivated type error preserves both choices without retrying with All', async () => {
    let count = 0;
    const p = page(async () => {
        count++;
        return { ok: false, headers: { get: () => 'application/json' }, json: async () => ({ message: 'Loại thẻ không hợp lệ. Vui lòng chọn một loại thẻ đang hoạt động hoặc Tất cả.' }) };
    }, '7');
    await p.ready;
    p.cardType.value = '1';
    await p.submit();
    assert.equal(count, 1);
    assert.equal(p.cardType.value, '1');
    assert.equal(p.input.value, '7');
    assert.match(p.message.textContent, /Loại thẻ không hợp lệ/);
    assert.deepEqual(p.downloads, []);
});
