// Temporary selection state only; the server independently validates every barcode.
class DirectLoanSelectionState {
    constructor(rows = []) {
        this.rows = rows.map(row => ({ ...row, version: 0, checking: false, editing: false }));
    }
    duplicate(code, exceptId) {
        return this.rows.some(row => row.rowId !== exceptId && row.copyCode.toUpperCase() === code.trim().toUpperCase());
    }
    add(rowId, code) {
        if (this.duplicate(code)) return null;
        const row = { rowId, copyCode: code.trim(), bookTitle: '', status: '', error: null, version: 1, checking: true, editing: false };
        this.rows.push(row);
        return row;
    }
    edit(rowId) {
        const row = this.rows.find(row => row.rowId === rowId);
        if (!row) return;
        row.version++;
        row.checking = false;
        row.editing = true;
    }
    check(rowId, code) {
        const row = this.rows.find(row => row.rowId === rowId);
        if (!row) return null;
        Object.assign(row, { copyCode: code.trim(), bookTitle: '', status: '', error: null, holdId: null, checking: true, editing: false, version: row.version + 1 });
        if (!row.copyCode || this.duplicate(row.copyCode, rowId)) {
            row.error = !row.copyCode ? 'Vui lòng nhập mã vạch bản sao.' : 'Mã vạch này đã có trong danh sách mượn.';
            row.checking = false;
        }
        return row;
    }
    apply(rowId, version, result) {
        const row = this.rows.find(row => row.rowId === rowId);
        if (!row || row.version !== version || row.editing) return false;
        Object.assign(row, { bookTitle: result.bookTitle || '', status: result.status || '', error: result.error || null, holdId: result.holdId ?? null, checking: false });
        return true;
    }
    remove(rowId) { this.rows = this.rows.filter(row => row.rowId !== rowId); }
    get counts() {
        const pending = this.rows.filter(row => row.checking || row.editing).length;
        const errors = this.rows.filter(row => !row.checking && !row.editing && row.error).length;
        return { total: this.rows.length, pending, errors, valid: this.rows.length - pending - errors };
    }
    canConfirm(remaining, readerValid) {
        const count = this.counts;
        return readerValid && count.valid > 0 && count.pending === 0 && count.errors === 0 && count.valid <= remaining;
    }
}
if (typeof module !== 'undefined') module.exports = DirectLoanSelectionState;
