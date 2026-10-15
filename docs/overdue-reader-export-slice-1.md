# Lát 1 — Xuất bạn đọc quá hạn và nợ phí

## Quy tắc đã xác nhận

- Xuất bạn đọc có sách quá hạn **hoặc** số dư phí dương. Không áp dụng bộ lọc màn hình hay phân trang.
- Một phiếu mượn tương ứng một cuốn, kể cả phiếu cũ chưa có `BookCopyId`. Chưa trả khi `ReturnedAtUtc == null`.
- `DueDate` là ngày, không phải thời điểm. Chỉ đếm phiếu có ngày đến hạn trước ngày nghiệp vụ và có ít nhất một ngày mở cửa trễ hạn. Ngày trễ là số ngày mở cửa sau hạn, đến hết ngày nghiệp vụ; loại ngày nghỉ tuần và ngày lễ theo lịch hiện hữu.
- Dùng `Asia/Ho_Chi_Minh`; chụp một thời điểm UTC cho ngày tính toán, tên tệp và nhật ký.
- Nợ là `max(ReaderAccount.OutstandingBalance, 0)`; không cộng thêm phí chưa ghi nhận. Thanh toán một phần, miễn hoặc hủy phải đã được phản ánh vào số dư. `ReaderAccount.Fees` không được ánh xạ vào CSDL nên không dùng làm nguồn xuất.
- Bạn đọc chỉ nợ phí có số cuốn và ngày trễ bằng 0; chưa có thẻ thì mã thẻ trống.
- Chỉ nhân sự hoạt động có role `LibraryManager` được xuất. `SystemAdmin` và `Librarian` không được xuất.
- Nhật ký ghi Thành công / Thất bại / Không có dữ liệu, người xuất từ phiên server, thời điểm UTC, Không lọc, số dòng và mã nhân sự. Thành công nghĩa là server đã chuẩn bị tệp, không xác nhận tải/mở tệp ở máy người dùng.
- Tệp chỉ được trả sau khi lưu nhật ký thành công. Lỗi nhật ký trả 500, không có nội dung Excel. Danh sách rỗng trả 422 với thông báo tiếng Việt.

## Thay đổi

Tạo `backend/Services/OverdueReaderExportService.cs` (tổng hợp và ghi nhật ký bắt buộc), `OverdueReaderWorkbook.cs` (ZIP/XML Open XML với chuỗi literal và ô số), `backend/Controllers/OverdueReaderExportController.cs` (POST API có phân quyền và chống CSRF), `frontend/wwwroot/js/overdue-reader-export.js` (trạng thái tải và lỗi), hai file test mới và tài liệu này.

Sửa `backend/Program.cs` chỉ để đăng ký service; `backend/Models/AuditLog.cs` thêm hành động vào bộ lọc nhật ký; test `AuditLogFilterTests.cs` cập nhật danh sách hành động mong đợi; `frontend/Views/OverdueLoan/Index.cshtml` thêm nút và mô tả xuất không lọc. Không sửa DbContext, schema, cấu hình, logic mượn/trả, logic danh sách quá hạn hiện hữu. Không thêm package hoặc migration. Không commit hay đổi nhánh.

Tái sử dụng `StaffOnlyAttribute`, xác thực cookie qua `IAuditLogService`, lịch mở cửa, bộ chuyển ngày Việt Nam, `AuditLogService.Create`, bảng và trang nhật ký hiện hữu. Không gọi `WriteAsync` cho xuất vì hàm đó nuốt lỗi ghi nhật ký; service xuất tự chờ `SaveChangesAsync` và chặn tệp nếu lỗi.

Truy vấn phiếu và bạn đọc riêng, tổng hợp theo `ReaderAccountId`, không nhân số dư khi một người có nhiều phiếu, không truy vấn riêng từng bạn đọc. Chỉ thêm nhật ký; không cập nhật khoản nợ hoặc trạng thái mượn/trả. Lịch hiện hữu có thể tự khởi tạo các ngày tuần còn thiếu theo hành vi của `WorkingScheduleService`.

## Chạy và demo

```powershell
dotnet run --project backend/Project.csproj
```

1. Dùng CSDL và cấu hình chạy hiện hữu. Đăng nhập tài khoản `LibraryManager`, vào `/OverdueLoan`.
2. Bấm **Xuất Excel**. Có thể chọn bộ lọc trên màn hình trước để xác nhận tệp vẫn xuất toàn bộ nhóm theo quy tắc trên.
3. Nút hiển thị **Đang xuất…**, vô hiệu hóa trong khi chờ rồi khôi phục. Tệp có tên `Ban-doc-qua-han-no-phi_dd-MM-yyyy.xlsx` theo ngày Việt Nam.
4. Mở bằng Microsoft Excel: kiểm tra đúng sáu cột Mã thẻ / Họ tên / Số điện thoại / Số cuốn quá hạn / Số ngày trễ dài nhất / Tổng phí đang nợ. Kiểm tra tiếng Việt, số 0 đầu, ô số và định dạng tiền đồng; đối chiếu dữ liệu nguồn.
5. Đăng nhập `SystemAdmin`, mở `/AuditLog`, chọn hành động **Xuất bạn đọc quá hạn và nợ phí**; đối chiếu người xuất, thời điểm hiển thị, Không lọc, kết quả và số dòng. Trang nhật ký hiện hiển thị giờ địa phương của máy chủ theo cơ chế sẵn có, bản ghi lưu UTC.
6. Kiểm tra `Librarian` và `SystemAdmin` không thấy nút; POST API `/api/overdue-readers/export` bằng tài khoản này bị 403, chưa đăng nhập bị 401. Request hợp lệ cần anti-forgery token của trang.
7. Trên CSDL demo không có bạn đọc phù hợp: hiển thị **Không có dữ liệu phù hợp để xuất**, không tải tệp. Dùng test interceptor để giả lập lỗi nhật ký mà không phá CSDL demo.

## Kiểm chứng thực tế

- `dotnet build backend/Project.csproj --no-restore`: đạt. Có cảnh báo từ ImageSharp hiện hữu về license; restore ban đầu cũng có cảnh báo không lấy được dữ liệu kiểm tra lỗ hổng NuGet.
- `dotnet test backend/tests/Project.Tests/Project.Tests.csproj --no-restore --filter FullyQualifiedName~OverdueReaderExportTests`: không biên dịch được do lỗi test có sẵn. Các test ActivityLog, AuditLog, ReaderRegistration, SecurityFix, ReaderAccountAdmin, ReaderPasswordReset, ReaderEmailVerification, ReaderBookHold gọi constructor thiếu `holdPickupConfirmationService`; ReaderBookHold còn dùng `ReaderRegistrationService.PickupDays` không tồn tại.
- Đã chạy riêng `OverdueReaderExportTests`, `OverdueLoanTests`, `AuditLogFilterTests`, `AuditLogProtectionTests` bằng project tạm trong `backend/tests/Project.Tests/obj/export-verification`: **57 đạt, 0 lỗi**. Project tạm dùng cùng package test, tham chiếu ứng dụng và bốn file test; không thay đổi cấu hình project chính.
- `node --test frontend/tests/overdue-reader-export.test.cjs`: **5 đạt**. Kiểm tra nút chờ/chặn bấm lặp, tên tệp server, lỗi JSON/rỗng/nhật ký/xác thực, lỗi mạng và khôi phục nút.
- `node --test frontend/tests/*.test.cjs`: test xuất đạt; bộ state mượn trực tiếp đạt 10/10; `direct-loan-recovery.test.cjs:68` lỗi `0 !== 1` ngoài phạm vi thay đổi.
- `git diff --check`: đạt.
- Tệp được đọc kiểm tra ZIP và XML trong test: đủ sáu cột, kiểu text/số, số 0 đầu, tiếng Việt, không có công thức. Chưa mở bằng Microsoft Excel, chưa kiểm tra trực tiếp trên trình duyệt hoặc CSDL SQL Server thật. Python trong môi trường không chạy được nên chưa kiểm tra bằng openpyxl.

## Giới hạn và nghiệm thu

Không đánh dấu hoàn tất nghiệm thu AC 1/3/4 trước demo trên CSDL thật và Microsoft Excel; không đánh dấu AC bộ lọc. Không làm chức năng gửi email, nhắc tự động, lọc xuất hay phân hệ quản trị mới.

Dữ liệu nghiệp vụ dùng cùng mốc ngày, nhưng các truy vấn không giữ một transaction snapshot xuyên suốt: cập nhật đồng thời trong lúc truy vấn vẫn có thể ảnh hưởng kết quả. Tệp được tạo trong bộ nhớ, phù hợp quy mô hiện tại; cần đánh giá thêm nếu dữ liệu rất lớn. Xuất dựa trên số dư đã ghi nhận nên không suy ra các khoản phí chưa có trong số dư. Không thay đổi cách tính ngày hay nhận diện trả sách của màn hình cũ; tệp dùng trường trả sách lưu thật nên có thể khác màn hình nếu màn hình cũ còn liệt kê phiếu đã trả.
