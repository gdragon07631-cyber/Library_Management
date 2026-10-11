const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const State = require('../wwwroot/js/direct-loan-state.js');

// Exercise the production event handlers with a minimal DOM and controlled network.
// These tests verify recovery behavior, not browser rendering.
class Element {
    constructor() { this.dataset = {}; this.children = []; this.events = {}; this.value = ''; this.rows = []; }
    addEventListener(name, handler) { this.events[name] = handler; }
    async fire(name) { return this.events[name]?.({ preventDefault() {} }); }
    append(child) { this.children.push(child); }
    setAttribute() {}
    focus() {}
    select() {}
    querySelector() { return this.button ??= new Element(); }
    querySelectorAll() { return []; }
    replaceChildren() { this.rows = []; }
    insertRow() { const row = new Element(); this.rows.push(row); return row; }
    insertCell() { const cell = new Element(); this.children.push(cell); return cell; }
}
function page(saved = null, completed = false) {
    const elements = Object.fromEntries(['confirmDirectLoan', 'addDirectLoanCopy', 'CardCode', 'CopyCode',
        'selectedCopies', 'selectionMessage', 'limitWarning', 'verifyDirectLoan', 'selectedCount', 'validCount',
        'errorCount', 'pendingCount', 'remainingSelection', 'rowWarning'].map(id => [id, new Element()]));
    const form = elements.confirmDirectLoan;
    form.elements = { CardCode: { value: 'CARD01' }, RequestId: { value: 'initial-key' } };
    form.dataset = { remaining: '5', readerValid: 'true', verifyOnly: 'false', retrying: 'false',
        completed: String(completed), statusUrl: '/DirectLoan/Status', indexUrl: '/DirectLoan' };
    form.action = '/DirectLoan/ConfirmMany';
    elements.CardCode.value = 'CARD01'; elements.CardCode.form = new Element();
    elements.addDirectLoanCopy.dataset.lookupUrl = '/DirectLoan/LookupCopy';
    elements.selectedCopies.rows = [{ dataset: { rowId: 'r1', code: 'COPY01', title: 'Book', status: 'Available' } }];
    let storage = saved ? JSON.stringify(saved) : null;
    let failStorage = false;
    const requests = [], responses = [], events = {}, destinations = [];
    const location = { origin: 'https://library.test', assign: url => destinations.push(String(url)) };
    const context = { document: { getElementById: id => elements[id], createElement: () => new Element() },
        DirectLoanSelectionState: State, URL, location, crypto: { randomUUID: () => 'new-key' },
        window: { location, addEventListener: (name, handler) => { events[name] = handler; } },
        sessionStorage: {
            getItem: () => storage,
            setItem: (_, value) => { if (failStorage) throw Error('Storage disabled'); storage = value; },
            removeItem: () => { storage = null; }
        },
        FormData: class { constructor(form) { this.requestId = form.elements.RequestId.value; } },
        fetch: async (url, options) => {
            requests.push({ url: String(url), options });
            const next = responses.shift();
            if (next instanceof Error) throw next;
            if (typeof next === 'function') return next();
            return { ok: true, json: async () => next };
        }
    };
    vm.runInNewContext(fs.readFileSync(require.resolve('../wwwroot/js/direct-loan.js'), 'utf8'), context);
    return { elements, form, requests, responses, events, destinations,
        saved: () => JSON.parse(storage), blockStorage: () => { failStorage = true; } };
}

module.exports = (async () => {
    let passed = 0;
    async function test(name, run) { await run(); passed++; console.log(`PASS ${name}`); }
    await test('double submit sends once and locks inputs until an outcome', async () => {
        const p = page(); let resolve;
        p.responses.push(() => new Promise(r => { resolve = r; }));
        const first = p.form.fire('submit');
        await p.form.fire('submit');
        assert.equal(p.requests.length, 1);
        assert.equal(p.elements.CardCode.disabled, true);
        resolve({ ok: true, json: async () => ({ isSuccess: false, errorMessage: 'Rolled back' }) });
        await first;
        assert.equal(p.saved().requestId, 'initial-key');
        assert.equal(p.saved().rows.length, 1);
        assert.equal(p.saved().phase, 'retry');
        assert.equal(p.form.querySelector().textContent, 'Thử lại');
    });
    await test('retry retains request key; editing after known failure replaces it', async () => {
        const p = page();
        p.responses.push({ isSuccess: false }, { isSuccess: false });
        await p.form.fire('submit'); await p.form.fire('submit');
        assert.deepEqual(p.requests.map(r => r.options.body.requestId), ['initial-key', 'initial-key']);
        const remove = p.elements.selectedCopies.rows[0].children.at(-1).children.find(e => e.textContent === 'Xóa');
        await remove.fire('click');
        assert.equal(p.form.elements.RequestId.value, 'new-key');
        assert.equal(p.saved(), null);
        assert.equal(p.elements.selectedCopies.rows.length, 0);
    });
    await test('lost response locks new submission until verification confirms rollback', async () => {
        const p = page(); p.responses.push(Error('Network lost'));
        await p.form.fire('submit'); await p.form.fire('submit');
        assert.equal(p.requests.length, 1);
        assert.equal(p.elements.verifyDirectLoan.hidden, false);
        assert.equal(p.elements.CardCode.disabled, true);
        assert.equal(p.saved().phase, 'pending');
        p.responses.push({ isSuccess: false, isUncertain: true });
        await p.elements.verifyDirectLoan.fire('click');
        assert.equal(p.elements.CardCode.disabled, true);
        p.responses.push({ isSuccess: false, isUncertain: false });
        await p.elements.verifyDirectLoan.fire('click');
        assert.equal(p.elements.CardCode.disabled, false);
        assert.equal(p.saved().phase, 'retry');
        assert.equal(p.form.elements.RequestId.value, 'initial-key');
    });
    await test('reload restores pending key and rows and checks status without POST', async () => {
        const p = page({ requestId: 'old-key', cardCode: 'CARD01', phase: 'pending',
            rows: [{ rowId: 'old-row', copyCode: 'COPY03', bookTitle: 'Book', status: 'Available', error: null }] });
        p.responses.push({ isSuccess: true, url: '/DirectLoan/Status?requestId=old-key' });
        p.events.pageshow();
        await new Promise(r => setImmediate(r));
        assert.equal(p.requests.length, 1);
        assert.equal(p.requests[0].options.method, undefined);
        assert.ok(p.requests[0].url.includes('requestId=old-key'));
        assert.equal(p.form.elements.RequestId.value, 'old-key');
        assert.equal(p.elements.selectedCopies.rows[0].dataset.rowId, 'old-row');
        assert.equal(p.destinations[0], '/DirectLoan/Status?requestId=old-key');
    });
    await test('completed receipt clears its recovery snapshot and starts a fresh key', async () => {
        const p = page({ requestId: 'initial-key', rows: [], cardCode: 'CARD01', phase: 'pending' }, true);
        p.events.pageshow();
        assert.equal(p.saved(), null);
        assert.equal(p.form.elements.RequestId.value, 'new-key');
        assert.equal(p.requests.length, 0);
    });
    await test('unavailable session storage prevents a request that cannot be recovered', async () => {
        const p = page(); p.blockStorage(); await p.form.fire('submit');
        assert.equal(p.requests.length, 0);
        assert.match(p.elements.selectionMessage.textContent, /khôi phục/);
    });
    console.log(`${passed} recovery tests passed.`);
})();
