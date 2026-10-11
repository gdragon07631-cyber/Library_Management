(() => {
    const form = document.getElementById("copyLoanLookupForm");
    if (!form) return;

    const input = document.getElementById("copyLoanBarcode");
    const button = document.getElementById("copyLoanLookupButton");
    const result = document.getElementById("copyLoanLookupResult");
    const label = button.querySelector(".lookup-button-label");
    const loading = button.querySelector(".lookup-loading");

    const showMessage = (kind, message) => {
        const alert = document.createElement("div");
        alert.className = `alert alert-${kind} mb-0`;
        alert.textContent = message;
        result.replaceChildren(alert);
    };

    const addDetail = (container, title, value) => {
        const row = document.createElement("p");
        row.className = "mb-1";
        const heading = document.createElement("strong");
        heading.textContent = `${title}: `;
        row.append(heading, document.createTextNode(value));
        container.append(row);
    };

    const formatDate = (date) => {
        if (typeof date !== "string") return "Không có dữ liệu";
        const [year, month, day] = date.split("-");
        return year && month && day ? `${day}/${month}/${year}` : date;
    };

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        result.replaceChildren();

        const barcode = input.value.trim();
        if (!barcode) {
            showMessage("warning", "Vui lòng nhập mã vạch bản sao.");
            input.focus();
            return;
        }
        if (barcode.length > 50) {
            showMessage("warning", "Mã vạch không được vượt quá 50 ký tự.");
            input.focus();
            return;
        }

        button.disabled = true;
        label.classList.add("d-none");
        loading.classList.remove("d-none");

        try {
            const url = new URL(form.dataset.lookupUrl, window.location.origin);
            url.searchParams.set("barcode", barcode);
            const response = await fetch(url, { headers: { Accept: "application/json" } });
            const payload = await response.json().catch(() => ({}));

            if (payload.status === "notFound") {
                showMessage("warning", payload.message || "Không tìm thấy bản sao có mã vạch này.");
                return;
            }
            if (payload.status === "notBorrowed") {
                showMessage("info", payload.message || "Bản sao này hiện không có ai mượn.");
                return;
            }
            if (!response.ok) {
                showMessage("danger", payload.message || "Không thể tra cứu lúc này. Vui lòng thử lại.");
                return;
            }
            if (payload.status !== "borrowed") {
                showMessage("danger", "Phản hồi tra cứu không hợp lệ. Vui lòng thử lại.");
                return;
            }

            const card = document.createElement("div");
            card.className = "alert alert-success mb-0";
            const title = document.createElement("h3");
            title.className = "h6 mb-2";
            title.textContent = "Đang có phiếu mượn mở";
            card.append(title);
            addDetail(card, "Mã vạch bản sao", payload.copyCode || barcode);
            addDetail(card, "Bạn đọc", payload.readerName || "Không có dữ liệu");
            addDetail(card, "Hạn trả", formatDate(payload.dueDate));
            addDetail(card, "Số ngày trễ", String(Math.max(0, Number(payload.overdueDays) || 0)));
            result.replaceChildren(card);
        } catch {
            showMessage("danger", "Không thể kết nối để tra cứu. Vui lòng thử lại.");
        } finally {
            button.disabled = false;
            label.classList.remove("d-none");
            loading.classList.add("d-none");
        }
    });
})();
