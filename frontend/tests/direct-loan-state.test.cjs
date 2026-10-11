const assert = require('node:assert/strict');
const State = require('../wwwroot/js/direct-loan-state.js');
const cases = [];
const test = (name, run) => cases.push({ name, run });
const valid = { bookTitle: 'Sách', status: 'Sẵn sàng', error: null };

test('mixed rows retain their IDs and independent results', () => {
    const state = new State();
    for (let i = 0; i < 5; i++) state.add(`r${i}`, `CODE${i}`);
    assert.equal(state.canConfirm(5, true), false);
    for (const i of [4, 0, 2]) state.apply(`r${i}`, 1, valid);
    for (const i of [3, 1]) state.apply(`r${i}`, 1, { error: 'Không hợp lệ' });
    assert.deepEqual(state.counts, { total: 5, pending: 0, errors: 2, valid: 3 });
    assert.deepEqual(state.rows.map(r => r.rowId), ['r0', 'r1', 'r2', 'r3', 'r4']);
    assert.equal(state.canConfirm(5, true), false);
});
test('editing an invalid row keeps its identity and can fix the error', () => {
    const state = new State();
    state.add('one', 'BAD'); state.apply('one', 1, { error: 'Không tồn tại' });
    state.edit('one'); assert.equal(state.canConfirm(5, true), false);
    const row = state.check('one', 'GOOD');
    state.apply('one', row.version, valid);
    assert.equal(state.rows.length, 1);
    assert.equal(state.rows[0].rowId, 'one');
    assert.equal(state.rows[0].copyCode, 'GOOD');
    assert.equal(state.canConfirm(5, true), true);
});
test('older responses cannot overwrite newer edits', () => {
    const state = new State(); state.add('one', 'OLD');
    state.edit('one'); const newer = state.check('one', 'NEW').version;
    state.apply('one', newer, valid);
    assert.equal(state.apply('one', 1, { error: 'Old failure' }), false);
    assert.equal(state.rows[0].copyCode, 'NEW');
    assert.equal(state.rows[0].error, null);
    state.edit('one');
    assert.equal(state.apply('one', newer, valid), false);
    assert.equal(state.counts.pending, 1);
});
test('deleting pending rows ignores late responses and permits re-entry', () => {
    const state = new State(); state.add('old', 'CODE'); state.remove('old');
    const replacement = state.add('new', 'CODE');
    assert.equal(state.apply('old', 1, valid), false);
    assert.equal(state.rows.length, 1);
    assert.equal(replacement.checking, true);
});
test('duplicates are blocked on add and flagged when editing', () => {
    const state = new State(); state.add('one', 'COPY01'); state.apply('one', 1, valid);
    assert.equal(state.add('duplicate', ' copy01 '), null);
    state.add('two', 'BAD');
    const duplicate = state.check('two', ' COPY01 ');
    assert.match(duplicate.error, /đã có trong danh sách/);
    assert.equal(state.counts.valid, 1);
    assert.equal(state.counts.errors, 1);
    assert.equal(state.canConfirm(5, true), false);
});
test('repeated invalid edits retain the error and block confirmation', () => {
    const state = new State(); state.add('one', 'BAD');
    const row = state.check('one', 'STILLBAD');
    state.apply('one', row.version, { error: 'Không tồn tại' });
    assert.equal(state.rows[0].rowId, 'one');
    assert.equal(state.counts.errors, 1);
    assert.equal(state.canConfirm(5, true), false);
});
test('removing errors preserves valid rows and recomputes counts', () => {
    const state = new State();
    for (const id of ['good', 'bad1', 'bad2']) state.add(id, id);
    state.apply('good', 1, valid);
    for (const id of ['bad1', 'bad2']) state.apply(id, 1, { error: 'Lỗi' });
    state.remove('bad1'); state.remove('bad2');
    assert.deepEqual(state.counts, { total: 1, valid: 1, pending: 0, errors: 0 });
    assert.equal(state.canConfirm(1, true), true);
    state.remove('good'); assert.equal(state.canConfirm(5, true), false);
});
test('remaining quota, reader validity and pending checks gate confirmation', () => {
    const state = new State();
    state.add('one', 'ONE'); state.apply('one', 1, valid);
    assert.equal(state.canConfirm(0, true), false);
    assert.equal(state.canConfirm(1, false), false);
    state.add('two', 'TWO'); assert.equal(state.canConfirm(5, true), false);
    state.apply('two', 1, valid);
    assert.equal(state.canConfirm(1, true), false);
    assert.equal(state.canConfirm(2, true), true);
});
for (const { name, run } of cases) { run(); console.log(`PASS ${name}`); }
console.log(`${cases.length}/${cases.length} frontend state tests passed`);
