(() => {
    const form = document.getElementById("copyLoanLookupForm");
    if (!form) return;

    const input = document.getElementById("copyLoanBarcode");
    const returnDateInput = document.getElementById("returnDatePreview");
    const lookupButton = document.getElementById("copyLoanLookupButton");
    const result = document.getElementById("copyLoanLookupResult");
    const lookupLabel = lookupButton.querySelector(".lookup-button-label");
    const lookupLoading = lookupButton.querySelector(".lookup-loading");
    const readerSearchInput = document.getElementById("readerLoanSearchInput");
    const readerSearchButton = document.getElementById("readerLoanSearchButton");
    const readerSearchResult = document.getElementById("readerLoanSearchResult");
    const readerSearchLabel = readerSearchButton?.querySelector(".reader-search-label");
    const readerSearchLoading = readerSearchButton?.querySelector(".reader-search-loading");
    const scanner = document.getElementById("returnSessionScanner");
    const activeSession = document.getElementById("returnSessionActive");
    const activeCount = document.getElementById("returnSessionCount");
    const activeRows = document.getElementById("returnSessionRows");
    const finishButton = document.getElementById("finishReturnSession");
    const summary = document.getElementById("returnSessionSummary");
    const summaryCount = document.getElementById("returnSessionSummaryCount");
    const summaryRows = document.getElementById("returnSessionSummaryRows");
    const startNewButton = document.getElementById("startNewReturnSession");
    const storageKey = "library-return-session-v1";

    const makeSession = () => ({
        id: window.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(16).slice(2)}`,
        status: "active",
        results: []
    });

    const readSession = () => {
        try {
            const value = JSON.parse(window.sessionStorage.getItem(storageKey) || "null");
            if (value && (value.status === "active" || value.status === "completed") && Array.isArray(value.results)) {
                return value;
            }
        } catch {
            // Keep this tab usable even when browser storage is unavailable or invalid.
        }
        return makeSession();
    };

    let currentSession = readSession();
    let returnInProgress = false;

    const persistSession = () => {
        try {
            window.sessionStorage.setItem(storageKey, JSON.stringify(currentSession));
        } catch {
            // Results remain available in memory for the current page.
        }
    };

    const message = (kind, text) => {
        const alert = document.createElement("div");
        alert.className = `alert alert-${kind} mb-0`;
        alert.textContent = text;
        result.replaceChildren(alert);
        return alert;
    };

    const detail = (container, title, value) => {
        const row = document.createElement("p");
        row.className = "mb-1";
        const label = document.createElement("strong");
        label.textContent = `${title}: `;
        row.append(label, document.createTextNode(value));
        container.append(row);
    };

    const formatDate = (date) => {
        if (typeof date !== "string") return "Không có dữ liệu";
        const [year, month, day] = date.split("-");
        return year && month && day ? `${day}/${month}/${year}` : date;
    };

    const formatTimestamp = (date) => {
        if (!date) return "Không có dữ liệu";
        const parsed = new Date(date);
        return Number.isNaN(parsed.getTime()) ? "Không có dữ liệu" : parsed.toLocaleString("vi-VN");
    };

    const renderRows = (tbody, entries, emptyMessage) => {
        tbody.replaceChildren();
        if (entries.length === 0) {
            const row = document.createElement("tr");
            const cell = document.createElement("td");
            cell.colSpan = 10;
            cell.className = "text-center text-muted";
            cell.textContent = emptyMessage;
            row.append(cell);
            tbody.append(row);
            return;
        }

        entries.forEach((entry, index) => {
            const row = document.createElement("tr");
            const values = [
                String(index + 1),
                entry.copyCode || "Không có dữ liệu",
                entry.bookTitle || "Không có dữ liệu",
                entry.readerName || "Không có dữ liệu",
                formatTimestamp(entry.returnedAtUtc),
                entry.result || "Đã nhận trả",
                `${new Intl.NumberFormat("vi-VN").format(Number(entry.overdueFee) || 0)} VNĐ`,
                entry.receivable ? `${entry.receivable.id} · ${new Intl.NumberFormat("vi-VN").format(Number(entry.receivable.amount) || 0)} VNĐ` : "—",
                entry.copyStatus || "Không có dữ liệu",
                entry.reservedFor
                    ? [entry.reservedFor.readerName, entry.reservedFor.readerCode].filter(Boolean).join(" — ")
                    : "—"
            ];
            for (const value of values) {
                const cell = document.createElement("td");
                cell.textContent = value;
                row.append(cell);
            }
            tbody.append(row);
        });
    };

    const renderSession = () => {
        const isActive = currentSession.status === "active";
        scanner.classList.toggle("d-none", !isActive);
        activeSession.classList.toggle("d-none", !isActive);
        summary.classList.toggle("d-none", isActive);
        lookupButton.disabled = !isActive;
        input.disabled = !isActive;
        activeCount.textContent = `${currentSession.results.length} cuốn đã nhận trả`;
        finishButton.disabled = currentSession.results.length === 0 || returnInProgress;
        renderRows(activeRows, currentSession.results, "Chưa nhận trả cuốn nào trong lượt này.");
        summaryCount.textContent = `${currentSession.results.length} cuốn đã nhận trả thành công trong lượt này.`;
        renderRows(summaryRows, currentSession.results, "Lượt này chưa có cuốn nào được nhận trả thành công.");
    };

    const showBorrowed = (loan, barcode) => {
        const card = document.createElement("div");
        card.className = "alert alert-success mb-0";
        const title = document.createElement("h3");
        title.className = "h6 mb-2";
        title.textContent = "Đang có phiếu mượn mở";
        card.append(title);
        detail(card, "Mã vạch bản sao", loan.copyCode || barcode);
        detail(card, "Mã phiếu mượn", String(loan.loanId));
        detail(card, "Tên sách", loan.bookTitle || "Không có dữ liệu");
        detail(card, "Bạn đọc", loan.readerName || "Không có dữ liệu");
        detail(card, "Hạn trả", formatDate(loan.dueDate));
        detail(card, "Số ngày trễ theo lịch mở cửa", String(Math.max(0, Number(loan.overdueDays) || 0)));
        detail(card, "Ngày trả thực tế (xem trước)", formatDate(loan.returnDate));
        detail(card, "Mức phí mỗi ngày", `${new Intl.NumberFormat("vi-VN").format(Number(loan.dailyOverdueFee) || 0)} VNĐ`);
        detail(card, "Tổng phí trễ hạn dự kiến", `${new Intl.NumberFormat("vi-VN").format(Number(loan.estimatedOverdueFee) || 0)} VNĐ`);

        const returnButton = document.createElement("button");
        returnButton.type = "button";
        returnButton.className = "btn btn-success mt-2";
        returnButton.textContent = "Xác nhận nhận trả";
        returnButton.addEventListener("click", () => confirmReturn(returnButton, loan, barcode));
        card.append(returnButton);
        result.replaceChildren(card);
    };

    const confirmReturn = async (button, loan, barcode) => {
        if (returnInProgress || !window.confirm("Xác nhận đã nhận trả bản sao này?")) return;
        returnInProgress = true;
        button.disabled = true;
        button.textContent = "Đang xử lý…";
        lookupButton.disabled = true;
        input.disabled = true;
        finishButton.disabled = true;
        const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value;

        try {
            const baseUrl = form.dataset.returnUrl.replace(/\/$/, "");
            const response = await fetch(`${baseUrl}/${encodeURIComponent(loan.loanId)}/return`, {
                method: "POST",
                headers: {
                    Accept: "application/json",
                    RequestVerificationToken: token || ""
                }
            });
            const payload = await response.json().catch(() => ({}));
            if (!response.ok || payload.success !== true) {
                button.disabled = false;
                button.textContent = "Xác nhận nhận trả";
                message("danger", payload.message || "Không thể xác nhận trả lúc này. Vui lòng tra cứu lại.");
                return;
            }

            const entry = {
                loanId: loan.loanId,
                copyCode: payload.copyCode || loan.copyCode || barcode,
                bookTitle: loan.bookTitle || "Không có dữ liệu",
                readerName: payload.readerName || loan.readerName || "Không có dữ liệu",
                returnedAtUtc: payload.returnedAtUtc || new Date().toISOString(),
                overdueFee: payload.overdueFee || 0,
                receivable: payload.receivable || null,
                result: "Đã nhận trả",
                copyStatus: payload.copyStatus || "Không có dữ liệu",
                reservedFor: payload.reservedFor || null
            };
            if (!currentSession.results.some(item => String(item.loanId) === String(entry.loanId))) {
                currentSession.results.push(entry);
                persistSession();
            }
            renderSession();

            const returned = document.createElement("div");
            returned.className = "alert alert-success mb-0";
            const title = document.createElement("h3");
            title.className = "h6 mb-2";
            title.textContent = payload.message || "Nhận trả sách thành công.";
            returned.append(title);
            detail(returned, "Mã vạch bản sao", entry.copyCode);
            detail(returned, "Tên sách", entry.bookTitle);
            detail(returned, "Bạn đọc", entry.readerName);
            detail(returned, "Hạn trả", formatDate(payload.dueDate));
            detail(returned, "Ngày trả thực tế", formatDate(payload.returnDate));
            detail(returned, "Thời điểm nhận trả", formatTimestamp(entry.returnedAtUtc));
            detail(returned, "Số ngày trễ theo lịch mở cửa", String(payload.overdueDays ?? 0));
            detail(returned, "Mức phí mỗi ngày", `${new Intl.NumberFormat("vi-VN").format(Number(payload.dailyOverdueFee) || 0)} VNĐ`);
            detail(returned, "Phí trễ hạn đã ghi nhận", `${new Intl.NumberFormat("vi-VN").format(Number(payload.overdueFee) || 0)} VNĐ`);
            detail(returned, "Trạng thái phiếu mượn", payload.loanStatus || "Đã trả");
            if (payload.receivable) {
                detail(returned, "Khoản phải thu", `#${payload.receivable.id} · ${new Intl.NumberFormat("vi-VN").format(Number(payload.receivable.amount) || 0)} VNĐ · ${payload.receivable.status}`);
            } else {
                detail(returned, "Khoản phải thu", "Không phát sinh");
            }
            detail(returned, "Trạng thái bản sao", entry.copyStatus);
            if (payload.hasWaitingHold && entry.reservedFor) {
                const identity = [entry.reservedFor.readerName, entry.reservedFor.readerCode]
                    .filter(Boolean).join(" — ");
                if (identity) detail(returned, "Dành cho bạn đọc", identity);
            }
            result.replaceChildren(returned);
            input.value = "";
            if (readerSearchInput?.value.trim()) {
                await searchReaders(readerSearchInput.value.trim(), true);
            }
        } catch {
            button.disabled = false;
            button.textContent = "Xác nhận nhận trả";
            message("danger", "Không thể kết nối để xác nhận trả. Vui lòng tra cứu lại.");
        } finally {
            returnInProgress = false;
            const isActive = currentSession.status === "active";
            input.disabled = !isActive;
            lookupButton.disabled = !isActive;
            if (readerSearchInput) readerSearchInput.disabled = !isActive;
            if (readerSearchButton) readerSearchButton.disabled = !isActive;
            finishButton.disabled = currentSession.results.length === 0;
            if (isActive) input.focus();
        }
    };

    const renderReaderSearchResults = (readers) => {
        if (!readerSearchResult) return;
        readerSearchResult.replaceChildren();

        const wrapper = document.createElement("div");
        wrapper.className = "d-flex flex-column gap-3";

        readers.forEach((reader) => {
            const card = document.createElement("div");
            card.className = "card border shadow-sm";

            const header = document.createElement("div");
            header.className = "card-header bg-light d-flex flex-wrap justify-content-between align-items-center gap-2";

            const info = document.createElement("div");
            const nameStrong = document.createElement("strong");
            nameStrong.textContent = reader.readerName || "Bạn đọc";
            const meta = document.createElement("span");
            meta.className = "text-muted ms-2 small";
            meta.textContent = [
                reader.readerCode ? `Mã: ${reader.readerCode}` : null,
                reader.cardCode ? `Thẻ: ${reader.cardCode}` : null,
                reader.email || null
            ].filter(Boolean).join(" · ");
            info.append(nameStrong, meta);

            const badgeWrap = document.createElement("div");
            badgeWrap.className = "d-flex align-items-center gap-2";
            const openBadge = document.createElement("span");
            openBadge.className = reader.openLoanCount > 0 ? "badge bg-primary" : "badge bg-secondary";
            openBadge.textContent = `Đang mượn: ${reader.openLoanCount} cuốn`;
            const totalBadge = document.createElement("span");
            totalBadge.className = "badge bg-light text-dark border";
            totalBadge.textContent = `Tổng phiếu: ${reader.totalLoanCount}`;
            badgeWrap.append(openBadge, totalBadge);

            header.append(info, badgeWrap);
            card.append(header);

            const body = document.createElement("div");
            body.className = "card-body p-0";

            if (!Array.isArray(reader.loans) || reader.loans.length === 0) {
                const empty = document.createElement("p");
                empty.className = "text-muted p-3 mb-0";
                empty.textContent = "Bạn đọc này chưa có lịch sử mượn sách.";
                body.append(empty);
            } else {
                const tableWrap = document.createElement("div");
                tableWrap.className = "table-responsive";
                const table = document.createElement("table");
                table.className = "table table-hover align-middle mb-0";

                const thead = document.createElement("thead");
                thead.className = "table-light";
                thead.innerHTML = "<tr><th>Mã phiếu</th><th>Mã vạch</th><th>Tên sách</th><th>Ngày mượn</th><th>Hạn trả</th><th>Trạng thái</th><th>Phí trễ hạn</th><th class=\"text-end\">Thao tác</th></tr>";
                table.append(thead);

                const tbody = document.createElement("tbody");
                reader.loans.forEach((loan) => {
                    const tr = document.createElement("tr");

                    const tdId = document.createElement("td");
                    tdId.textContent = `#${loan.loanId}`;

                    const tdCopy = document.createElement("td");
                    const codeEl = document.createElement("code");
                    codeEl.textContent = loan.copyCode || "Chưa gán";
                    tdCopy.append(codeEl);

                    const tdTitle = document.createElement("td");
                    tdTitle.className = "fw-semibold";
                    tdTitle.textContent = loan.bookTitle || "Không có dữ liệu";

                    const tdLoanDate = document.createElement("td");
                    tdLoanDate.textContent = formatDate(loan.loanDate);

                    const tdDueDate = document.createElement("td");
                    tdDueDate.textContent = formatDate(loan.dueDate);

                    const tdStatus = document.createElement("td");
                    const statusBadge = document.createElement("span");
                    if (loan.isOpen) {
                        if (Number(loan.overdueDays) > 0) {
                            statusBadge.className = "badge bg-danger";
                            statusBadge.textContent = `Đang mượn · Quá hạn ${loan.overdueDays} ngày`;
                        } else {
                            statusBadge.className = "badge bg-warning text-dark";
                            statusBadge.textContent = "Đang mượn";
                        }
                    } else {
                        statusBadge.className = "badge bg-success";
                        const returnedText = loan.returnedAtUtc ? ` (${formatTimestamp(loan.returnedAtUtc)})` : "";
                        statusBadge.textContent = `${loan.closeReason || "Đã trả"}${returnedText}`;
                    }
                    tdStatus.append(statusBadge);

                    const tdFee = document.createElement("td");
                    const feeAmount = Number(loan.estimatedOverdueFee) || 0;
                    tdFee.textContent = feeAmount > 0
                        ? `${new Intl.NumberFormat("vi-VN").format(feeAmount)} VNĐ${loan.isOpen ? " (dự kiến)" : ""}`
                        : "0 VNĐ";
                    if (feeAmount > 0) tdFee.className = "text-danger fw-semibold";

                    const tdAction = document.createElement("td");
                    tdAction.className = "text-end";
                    if (loan.isOpen && loan.copyCode) {
                        const btnGroup = document.createElement("div");
                        btnGroup.className = "d-inline-flex gap-1";

                        const selectBtn = document.createElement("button");
                        selectBtn.type = "button";
                        selectBtn.className = "btn btn-sm btn-outline-primary";
                        selectBtn.textContent = "Xem trước";
                        selectBtn.addEventListener("click", () => {
                            input.value = loan.copyCode;
                            showBorrowed(loan, loan.copyCode);
                            result.scrollIntoView({ behavior: "smooth", block: "nearest" });
                        });

                        const returnNowBtn = document.createElement("button");
                        returnNowBtn.type = "button";
                        returnNowBtn.className = "btn btn-sm btn-success";
                        returnNowBtn.textContent = "Nhận trả";
                        returnNowBtn.addEventListener("click", () => {
                            confirmReturn(returnNowBtn, loan, loan.copyCode);
                        });

                        btnGroup.append(selectBtn, returnNowBtn);
                        tdAction.append(btnGroup);
                    } else {
                        const doneSpan = document.createElement("span");
                        doneSpan.className = "text-muted small";
                        doneSpan.textContent = "—";
                        tdAction.append(doneSpan);
                    }

                    tr.append(tdId, tdCopy, tdTitle, tdLoanDate, tdDueDate, tdStatus, tdFee, tdAction);
                    tbody.append(tr);
                });
                table.append(tbody);
                tableWrap.append(table);
                body.append(tableWrap);
            }

            card.append(body);
            wrapper.append(card);
        });

        readerSearchResult.append(wrapper);
    };

    const searchReaders = async (queryText, silent = false) => {
        if (!readerSearchResult || !form.dataset.readerSearchUrl) return;
        const term = (queryText ?? readerSearchInput?.value ?? "").trim();
        if (!term) {
            if (!silent) {
                const warn = document.createElement("div");
                warn.className = "alert alert-warning mb-0";
                warn.textContent = "Vui lòng nhập tên bạn đọc, mã bạn đọc, mã thẻ hoặc email.";
                readerSearchResult.replaceChildren(warn);
                readerSearchInput?.focus();
            }
            return;
        }

        if (readerSearchButton) readerSearchButton.disabled = true;
        readerSearchLabel?.classList.add("d-none");
        readerSearchLoading?.classList.remove("d-none");

        try {
            const url = new URL(form.dataset.readerSearchUrl, window.location.origin);
            url.searchParams.set("query", term);
            if (returnDateInput?.value) url.searchParams.set("returnDate", returnDateInput.value);
            const response = await fetch(url, { headers: { Accept: "application/json" } });
            const payload = await response.json().catch(() => ({}));

            if (payload.status === "notFound") {
                const info = document.createElement("div");
                info.className = "alert alert-warning mb-0";
                info.textContent = payload.message || "Không tìm thấy bạn đọc phù hợp.";
                readerSearchResult.replaceChildren(info);
            } else if (payload.status === "ok" && Array.isArray(payload.readers)) {
                renderReaderSearchResults(payload.readers);
            } else {
                const err = document.createElement("div");
                err.className = "alert alert-danger mb-0";
                err.textContent = payload.message || "Không thể tìm kiếm bạn đọc lúc này.";
                readerSearchResult.replaceChildren(err);
            }
        } catch {
            const err = document.createElement("div");
            err.className = "alert alert-danger mb-0";
            err.textContent = "Không thể kết nối để tìm kiếm bạn đọc. Vui lòng thử lại.";
            readerSearchResult.replaceChildren(err);
        } finally {
            if (readerSearchButton) readerSearchButton.disabled = currentSession.status !== "active";
            readerSearchLabel?.classList.remove("d-none");
            readerSearchLoading?.classList.add("d-none");
        }
    };

    readerSearchButton?.addEventListener("click", () => {
        searchReaders(readerSearchInput?.value);
    });

    readerSearchInput?.addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
            event.preventDefault();
            searchReaders(readerSearchInput.value);
        }
    });

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        result.replaceChildren();
        const barcode = input.value.trim();
        if (!barcode) {
            if (readerSearchInput?.value.trim()) {
                await searchReaders(readerSearchInput.value.trim());
                return;
            }
            message("warning", "Vui lòng nhập mã vạch bản sao.");
            input.focus();
            return;
        }
        if (barcode.length > 50) {
            message("warning", "Mã vạch không được vượt quá 50 ký tự.");
            input.focus();
            return;
        }

        lookupButton.disabled = true;
        lookupLabel.classList.add("d-none");
        lookupLoading.classList.remove("d-none");
        try {
            const url = new URL(form.dataset.lookupUrl, window.location.origin);
            url.searchParams.set("barcode", barcode);
            if (returnDateInput?.value) url.searchParams.set("returnDate", returnDateInput.value);
            const response = await fetch(url, { headers: { Accept: "application/json" } });
            const payload = await response.json().catch(() => ({}));
            if (payload.status === "notFound") {
                message("warning", payload.message || "Không tìm thấy bản sao có mã vạch này.");
                if (readerSearchInput && form.dataset.readerSearchUrl) {
                    readerSearchInput.value = barcode;
                    await searchReaders(barcode, true);
                }
            } else if (payload.status === "notBorrowed") {
                message("info", payload.message || "Bản sao này hiện không có ai mượn.");
            } else if (payload.status === "borrowed" && response.ok) {
                showBorrowed(payload, barcode);
            } else {
                message("danger", payload.message || "Không thể tra cứu lúc này. Vui lòng thử lại.");
            }
        } catch {
            message("danger", "Không thể kết nối để tra cứu. Vui lòng thử lại.");
        } finally {
            lookupButton.disabled = currentSession.status !== "active";
            lookupLabel.classList.remove("d-none");
            lookupLoading.classList.add("d-none");
            input.focus();
        }
    });

    returnDateInput?.addEventListener("change", () => {
        if (input.value.trim()) form.requestSubmit();
        if (readerSearchInput?.value.trim()) searchReaders(readerSearchInput.value.trim(), true);
    });

    finishButton.addEventListener("click", () => {
        if (currentSession.results.length === 0) return;
        currentSession.status = "completed";
        persistSession();
        renderSession();
    });

    startNewButton.addEventListener("click", () => {
        currentSession = makeSession();
        persistSession();
        result.replaceChildren();
        readerSearchResult?.replaceChildren();
        if (readerSearchInput) readerSearchInput.value = "";
        renderSession();
        input.focus();
    });

    renderSession();
    if (currentSession.status === "active") input.focus();
})();
