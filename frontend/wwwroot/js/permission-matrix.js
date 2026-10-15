(() => {
    const toggle = document.getElementById('matrix-filter-toggle');
    const table = document.getElementById('permission-matrix');
    if (!toggle || !table) return;
    toggle.addEventListener('click', () => {
        const active = table.classList.toggle('show-differences-only');
        toggle.setAttribute('aria-pressed', String(active));
        toggle.textContent = active ? 'Hiển thị tất cả' : 'Chỉ xem sai lệch';
    });
})();
