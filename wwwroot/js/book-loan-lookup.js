(() => {
    const form = document.getElementById("copyLoanLookupForm");
    if (!form) return;

    const input = document.getElementById("copyLoanBarcode");
    const lookupButton = document.getElementById("copyLoanLookupButton");
    const result = document.getElementById("copyLoanLookupResult");
    const lookupLabel = lookupButton.querySelector(".lookup-button-label");
    const lookupLoading = lookupButton.querySelector(".lookup-loading");

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

    const showBorrowed = (loan, barcode) => {
        const card = document.createElement("div");
        card.className = "alert alert-success mb-0";
        const title = document.createElement("h3");
        title.className = "h6 mb-2";
        title.textContent = "Đang có phiếu mượn mở";
        card.append(title);
        detail(card, "Mã vạch bản sao", loan.copyCode || barcode);
        detail(card, "Bạn đọc", loan.readerName || "Không có dữ liệu");
        detail(card, "Hạn trả", formatDate(loan.dueDate));
        detail(card, "Số ngày trễ", String(Math.max(0, Number(loan.overdueDays) || 0)));

        const returnButton = document.createElement("button");
        returnButton.type = "button";
        returnButton.className = "btn btn-success mt-2";
        returnButton.textContent = "Xác nhận nhận trả";
        returnButton.addEventListener("click", () => confirmReturn(returnButton, loan, barcode));
        card.append(returnButton);
        result.replaceChildren(card);
    };

    const confirmReturn = async (button, loan, barcode) => {
        if (!window.confirm("Xác nhận đã nhận trả bản sao này?")) return;
        button.disabled = true;
        button.textContent = "Đang xử lý…";
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

            const returned = document.createElement("div");
            returned.className = "alert alert-success mb-0";
            const title = document.createElement("h3");
            title.className = "h6 mb-2";
            title.textContent = payload.message || "Nhận trả sách thành công.";
            returned.append(title);
            detail(returned, "Mã vạch bản sao", payload.copyCode || barcode);
            detail(returned, "Bạn đọc", payload.readerName || loan.readerName || "Không có dữ liệu");
            detail(returned, "Ngày trả", payload.returnedAtUtc ? new Date(payload.returnedAtUtc).toLocaleString("vi-VN") : "Đã ghi nhận");
            detail(returned, "Trạng thái phiếu mượn", payload.loanStatus || "Đã trả");
            detail(returned, "Trạng thái bản sao", payload.copyStatus || "Sẵn sàng");
            result.replaceChildren(returned);
        } catch {
            button.disabled = false;
            button.textContent = "Xác nhận nhận trả";
            message("danger", "Không thể kết nối để xác nhận trả. Vui lòng tra cứu lại.");
        }
    };

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        result.replaceChildren();
        const barcode = input.value.trim();
        if (!barcode) {
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
            const response = await fetch(url, { headers: { Accept: "application/json" } });
            const payload = await response.json().catch(() => ({}));
            if (payload.status === "notFound") {
                message("warning", payload.message || "Không tìm thấy bản sao có mã vạch này.");
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
            lookupButton.disabled = false;
            lookupLabel.classList.remove("d-none");
            lookupLoading.classList.add("d-none");
        }
    });
})();
