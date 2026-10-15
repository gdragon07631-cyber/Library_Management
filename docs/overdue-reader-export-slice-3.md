# Lát 3 — Lọc loại thẻ và kết hợp điều kiện

## Quy tắc đã xác nhận

- Chỉ chọn loại thẻ có `LibraryCardType.IsActive == true`. Backend kiểm tra lại ngay khi xử lý xuất, từ chối loại không tồn tại/đã ngừng sử dụng; không tự chuyển thành Tất cả.
- Lọc theo loại của `ReaderAccount.LibraryCard` hiện tại, kể cả thẻ hết hạn hoặc bị khóa. Nếu đổi loại, dùng loại hiện tại; phiếu mượn không lưu loại thẻ lịch sử.
- CSDL hiện có quan hệ một-một và unique index trên `LibraryCard.ReaderAccountId`, ngăn nhiều thẻ cho một bạn đọc. Không thêm quy tắc chọn một thẻ bất kỳ.
- Tất cả bỏ hoàn toàn điều kiện loại thẻ; bạn đọc thiếu thẻ hoặc thuộc loại ngừng sử dụng vẫn được xét theo nhóm quá hạn HOẶC nợ phí của Lát 1.
- Điều kiện loại thẻ AND ngưỡng ngày trễ của Lát 2. Bỏ trống ngày trễ là không lọc ngày, 0 là ngưỡng 0. Bạn đọc chỉ nợ có 0 ngày trễ nên bị loại với ngưỡng dương.
- Nhật ký lưu ID và tên loại thẻ từ backend tại thời điểm xuất, hoặc Tất cả, cùng ngưỡng thực tế. Đổi tên loại thẻ sau đó không thay đổi nhật ký cũ.

## File thay đổi và tái sử dụng

Tạo `docs/overdue-reader-export-slice-3.md`.

Sửa các file chức năng xuất hiện có trong working tree:

- `backend/Controllers/OverdueReaderExportController.cs`: thêm GET `/api/overdue-readers/card-types`, dùng `IReaderRegistrationService.GetActiveCardTypesAsync`, chỉ trả ID/tên. Cả GET và POST dùng `StaffOnly(LibraryManager)` hiện hữu. POST xuất nhận thêm trường form tùy chọn `libraryCardTypeId`.
- `backend/Models/OverdueReaderExportFilter.cs`: thêm kiểm tra ID nguyên dương trong phạm vi Int32; null/trống/chuỗi trắng là Tất cả; 0 không phải Tất cả. Đây là helper riêng cho xuất, không phải entity chung.
- `backend/Services/OverdueReaderExportService.cs`: kiểm tra loại tồn tại/đang hoạt động; thêm điều kiện trên thẻ hiện tại khi chọn tập bạn đọc. Giữ nguyên tổng hợp toàn bộ phiếu, cách tính ngày mở cửa, số dư và thứ tự tạo tệp → lưu log → trả tệp. Không truy vấn riêng từng bạn đọc.
- `frontend/Views/OverdueLoan/Index.cshtml`: thêm ô Loại thẻ cạnh ngưỡng ngày trễ; nút thử lại khi tải danh sách thất bại; chỉ hiển thị khối xuất cho quản lý như trước.
- `frontend/wwwroot/js/overdue-reader-export.js`: nạp lựa chọn từ API, không hardcode loại thẻ; chặn xuất trước khi tải thành công, hiển thị lỗi/thử lại. Chụp cả hai điều kiện vào FormData khi bấm xuất; giữ lựa chọn sau mọi kết quả và không tự xuất lại với Tất cả khi backend từ chối loại.
- `backend/tests/Project.Tests/OverdueReaderExportTests.cs`: bổ sung danh sách loại, từng loại, AND, Tất cả, ngưỡng 0/trống/dương, thẻ thiếu/hết hạn/bị khóa/đổi loại, tên lịch sử trong log, unique thẻ, loại không hợp lệ/ngừng hoạt động, request cũ và lỗi log.
- `frontend/tests/overdue-reader-export.test.cjs`: bổ sung tải danh sách, Tất cả, tải lỗi/thử lại, hai điều kiện được chụp cùng lúc và giữ lựa chọn khi lỗi loại thẻ.

Lát 3 không sửa thêm Program.cs, DbContext, entity dùng chung, cấu hình hay schema. Không thêm thư viện/migration; không đổi nhánh/commit/merge. Các sửa đổi từ Lát 1–2 vẫn được giữ nguyên. Bộ tạo workbook không thay đổi: đúng sáu cột, không thêm cột loại thẻ.

## API và nhật ký

```text
GET /api/overdue-readers/card-types
=> [{ "id": 1, "name": "Tên loại thực tế" }]

POST /api/overdue-readers/export
minimumOverdueDays=7
libraryCardTypeId=1
```

GET/POST đều cần phiên nhân sự `LibraryManager`; POST tiếp tục cần anti-forgery token. Client chỉ gửi ID, không gửi tên làm khóa. Không có `libraryCardTypeId` tiếp tục là Tất cả, giữ khả năng dùng request cũ.

Nhật ký ví dụ:

```text
Loại thẻ: Tên loại thực tế (ID: 1); Số ngày trễ tối thiểu: 7 ngày; Kết quả: Thành công; Số dòng: 2; Mã nhân sự: 42
Loại thẻ: Tất cả; Không lọc (không áp dụng bộ lọc ngày trễ); ...
```

Tham số sai/loại không được phép: 400 JSON, không trả tệp hay ghi nhận yêu cầu xuất hợp lệ. Loại hợp lệ nhưng không có kết quả: 422 JSON `Không có dữ liệu phù hợp với điều kiện đã chọn`, nhật ký Không có dữ liệu. Lỗi kỹ thuật khi xác minh loại thẻ: 500, ghi technical log và không trả tệp. Lỗi lưu nhật ký sau chuẩn bị: 500 và không trả tệp như trước.

## Kiểm thử thực tế

```powershell
dotnet build backend/Project.csproj --no-restore
dotnet test backend/tests/Project.Tests/obj/export-verification/ExportVerification.csproj --no-restore
node --test frontend/tests/overdue-reader-export.test.cjs
git diff --check
```

- Build đạt, 0 lỗi; cảnh báo license ImageSharp hiện hữu vẫn còn.
- **105 test backend đạt**, gồm `OverdueReaderExportTests`, `OverdueLoanTests`, `AuditLogFilterTests`, `AuditLogProtectionTests`.
- **23 test JavaScript xuất đạt**.
- `git diff --check` đạt.

Project tạm `ExportVerification.csproj` đã có từ Lát 1 dưới `obj`, không nằm trong Git; dùng cùng xUnit/SQLite và tham chiếu project ứng dụng cùng bốn file test trên. Các lỗi biên dịch test đăng ký bạn đọc ở project test chính đã ghi trong tài liệu Lát 1–2 vẫn nằm ngoài phạm vi; không sửa hoặc chạy lại toàn bộ bộ test trong Lát 3.

Test kết hợp gọi controller/service thật với SQLite, đọc ZIP/XML workbook để đối chiếu dòng, số liệu, sáu cột và nhật ký; test JavaScript chạy event handler thật với DOM/fetch giả lập. Hồi quy ngày trễ, số 0 đầu, tiếng Việt, kiểu ô, tên tệp, quyền và lỗi log nằm trong các test đã chạy. Chưa thử HTTP thực tế qua trình duyệt, SQL Server thật hoặc Microsoft Excel; không kết luận toàn bộ story đã nghiệm thu.

## Chạy và demo thủ công

```powershell
dotnet run --project backend/Project.csproj
```

1. Đăng nhập Quản lý thư viện, mở `/OverdueLoan`. Danh sách loại thẻ nạp từ dữ liệu thực tế, mặc định Tất cả; nếu tải lỗi, bấm Thử tải lại loại thẻ.
2. Chọn một loại đang hoạt động thực tế (ví dụ Sinh viên nếu đã có), nhập 7 và bấm Xuất Excel.
3. Mở tệp `Ban-doc-qua-han-no-phi_dd-MM-yyyy.xlsx` bằng Microsoft Excel. Kiểm tra bạn đọc đồng thời thuộc loại đã chọn và trễ dài nhất ít nhất 7 ngày mở cửa; số cuốn bao gồm toàn bộ phiếu quá hạn của họ, phí không bị nhân đôi. Kiểm tra đủ sáu cột, tiếng Việt, số 0 đầu và ô tiền.
4. Kiểm tra hai điều kiện vẫn giữ trên màn hình. Có thể sửa lựa chọn trong lúc chờ: tệp và nhật ký của request đang chạy dùng bộ điều kiện tại lúc bấm; lựa chọn mới để dùng lần tiếp theo.
5. Đăng nhập SystemAdmin tại `/AuditLog`, chọn hành động xuất, kiểm tra ID/tên loại thẻ, ngưỡng 7, người xuất, thời điểm, số dòng và kết quả. SystemAdmin vẫn không có quyền xuất.
6. Chọn Tất cả, giữ ngưỡng 7 và xuất lại: vẫn chỉ gồm người đạt ngưỡng. Chọn loại cụ thể với ngày trễ trống/0: có xét người chỉ nợ phí thuộc loại đó.
7. Thử tổ hợp không có kết quả: thông báo đúng, không tải JSON thành Excel, giữ cả hai điều kiện. Các trường hợp loại bị ngừng sử dụng và lỗi log có test tự động; không sửa dữ liệu môi trường dùng chung để giả lập.

Không tự tạo dữ liệu nghiệp vụ chỉ để demo. Dùng loại thẻ và tài khoản có sẵn trong môi trường demo riêng.

## Giới hạn và ảnh hưởng

Không thay đổi quản lý loại thẻ, lịch mở cửa hoặc màn hình danh sách phiếu mượn. Có thêm một GET khi tải khối xuất và một truy vấn xác thực loại cụ thể trước xuất. Tập phiếu dùng để tổng hợp không bị thu hẹp bởi loại thẻ; quan hệ một-một tránh nhân dòng.

Các truy vấn không được giữ trong một transaction snapshot, nên chỉnh sửa thẻ/loại trong lúc request đang chạy có thể ảnh hưởng dữ liệu đọc; backend xác thực lại tại lúc xử lý và lưu tên vừa đọc vào log. Những giới hạn bộ nhớ/quy mô của Lát 1 vẫn còn. Chưa kiểm chứng giao diện thật, SQL Server và Excel như nêu trên.

Đã triển khai phần lọc loại thẻ/kết hợp của AC 2 và điều kiện nhật ký của AC 4; các test liên quan của cả ba lát đã chạy chung. Chưa đánh dấu toàn bộ AC 1–4 hoặc story hoàn tất nghiệm thu trước khi kiểm tra tích hợp thủ công trong môi trường thật.
