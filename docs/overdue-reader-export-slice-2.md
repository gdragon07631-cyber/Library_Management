# Lát 2 — Lọc theo số ngày trễ tối thiểu

## Quy tắc đã chốt

- Lọc theo `MaxDaysOverdue` của mỗi bạn đọc, **sau khi tổng hợp đầy đủ** theo Lát 1. Giữ nguyên ngày mở cửa, múi giờ Việt Nam, nhóm quá hạn HOẶC nợ phí và toàn bộ số liệu trong dòng.
- Bỏ trống/chuỗi trắng/không gửi tham số: không áp dụng bộ lọc ngày trễ.
- Nhập 0: áp dụng điều kiện `MaxDaysOverdue >= 0`; nhật ký ghi ngưỡng 0, phân biệt với không lọc.
- Bạn đọc chỉ nợ phí có 0 ngày trễ: được xuất khi bỏ trống hoặc nhập 0, bị loại với ngưỡng dương.
- Chỉ nhận chuỗi chữ số biểu diễn số nguyên 0–2147483647, có thể có khoảng trắng hai đầu. Không nhận số âm, dấu cộng, thập phân (kể cả `7.0`), ký tự, ký hiệu khoa học hoặc số vượt phạm vi. Không đặt giới hạn nghiệp vụ nhỏ hơn `Int32.MaxValue`.

## File và phạm vi thay đổi

Tạo:

- `backend/Models/OverdueReaderExportFilter.cs`: kiểm tra và chuyển tham số trống/0/số nguyên; model riêng cho xuất, không phải entity EF.
- `docs/overdue-reader-export-slice-2.md`: tài liệu này.

Sửa trên code Lát 1 đang có trong working tree:

- `backend/Controllers/OverdueReaderExportController.cs`: mở rộng cùng POST `/api/overdue-readers/export`, nhận trường form tùy chọn `minimumOverdueDays`; tham số sai trả 400 JSON, không xuất tệp hoặc tạo nhật ký xuất hợp lệ.
- `backend/Services/OverdueReaderExportService.cs`: tham số số nguyên nullable tùy chọn, lọc kết quả của `GetRowsAsync` trước khi tạo Excel; giữ nguyên hàm tổng hợp. Nhật ký dùng điều kiện thực tế thay cho chuỗi cố định.
- `frontend/Views/OverdueLoan/Index.cshtml`: ô nhập có nhãn/hướng dẫn gần nút xuất; giải thích bộ lọc danh sách phía dưới không áp dụng cho xuất.
- `frontend/wwwroot/js/overdue-reader-export.js`: kiểm tra giá trị trước khi gửi, bảo toàn 0 và chụp giá trị vào FormData trước khi chờ mạng. Ô nhập vẫn cho sửa; chỉnh sửa sau đó chỉ dùng cho lần xuất tiếp theo. Không xóa giá trị sau thành công/lỗi/rỗng.
- `backend/tests/Project.Tests/OverdueReaderExportTests.cs`: kiểm tra biên 6/7/8 ngày, toàn bộ số liệu nhiều phiếu, bỏ trống/0/chỉ nợ, tham số không hợp lệ gọi trực tiếp controller, nhật ký cùng ngưỡng với tệp và chặn tệp khi log lỗi; cập nhật thông báo rỗng theo Lát 2.
- `frontend/tests/overdue-reader-export.test.cjs`: kiểm tra không gửi giá trị sai, gửi 0, bỏ trống, giữ giá trị, chụp request khi đang chờ, phản hồi lỗi và chống bấm lặp.

Không sửa thêm Program.cs, DbContext, model dùng chung, cấu hình hay schema trong Lát 2. Không thêm package/migration, không viết lại bộ tạo Excel, phân quyền hoặc cơ chế lưu nhật ký. Các thay đổi Lát 1 chưa commit được giữ nguyên.

## API và nhật ký

POST multipart/form-data như Lát 1, với anti-forgery token và phiên đăng nhập `LibraryManager` hiện hữu:

```text
minimumOverdueDays=7
```

Request cũ không có trường này tiếp tục xuất không lọc. Với ngưỡng, điều kiện là `MaxDaysOverdue >= N`, không lọc các phiếu trước khi tổng hợp.

- Không ngưỡng: `Không lọc (không áp dụng bộ lọc ngày trễ)`.
- Có ngưỡng: `Số ngày trễ tối thiểu: N ngày`, kể cả N = 0.
- Kèm người xuất từ phiên server, thời điểm, kết quả, số dòng và mã nhân sự như Lát 1.
- Tham số hợp lệ nhưng không có dòng đạt ngưỡng: 422 JSON `Không có dữ liệu phù hợp với điều kiện đã chọn`, ghi nhật ký Không có dữ liệu và số dòng 0.
- Lỗi lưu nhật ký: 500 JSON, không trả tệp. Luồng thành công vẫn tạo xong Excel → lưu log → trả tệp.

## Kiểm thử thực tế

```powershell
dotnet build backend/Project.csproj --no-restore
dotnet test backend/tests/Project.Tests/obj/export-verification/ExportVerification.csproj --no-restore
node --test frontend/tests/overdue-reader-export.test.cjs
git diff --check
```

- Build đạt, 0 lỗi; còn cảnh báo license ImageSharp từ package hiện hữu.
- **83 test backend đạt**, gồm toàn bộ `OverdueReaderExportTests`, `OverdueLoanTests`, `AuditLogFilterTests`, `AuditLogProtectionTests`.
- **19 test frontend xuất đạt**.
- `git diff --check` đạt.

Project `ExportVerification.csproj` là project tạm đã tạo ở Lát 1 dưới `obj` (không quản lý bằng Git), dùng cùng xUnit/SQLite và tham chiếu ứng dụng cùng bốn file test nêu trên. Toàn bộ project test chính đã bị chặn ở Lát 1 bởi các test đăng ký bạn đọc dùng constructor cũ và hằng `PickupDays` không còn tồn tại; không sửa các test ngoài phạm vi đó, không chạy lại toàn bộ bộ test trong Lát 2. Khi các lỗi đó được sửa, có thể chạy test trong project chính:

```powershell
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --filter "FullyQualifiedName~OverdueReaderExportTests|FullyQualifiedName~OverdueLoanTests|FullyQualifiedName~AuditLogFilterTests|FullyQualifiedName~AuditLogProtectionTests"
```

Test đọc ZIP/XML của XLSX để kiểm tra sáu cột, kiểu ô, số 0 đầu, tiếng Việt, không có công thức, tên tệp/ngày nghiệp vụ. Test controller gọi action trực tiếp và kiểm tra file/nhật ký, không phải kiểm thử HTTP qua trình duyệt. Chưa chạy trên SQL Server thật hoặc mở Microsoft Excel trong lượt này.

## Chạy và demo

```powershell
dotnet run --project backend/Project.csproj
```

1. Đăng nhập Quản lý thư viện và vào `/OverdueLoan`. Dùng dữ liệu demo có bạn đọc trễ dài nhất 6, 7, 8 ngày mở cửa; một người có cả phiếu trễ 7 và 2 ngày; một người chỉ nợ phí.
2. Nhập **7**, bấm **Xuất Excel**. Kiểm tra ô vẫn giữ 7; tệp chỉ có người trễ dài nhất ít nhất 7 ngày. Người có hai phiếu vẫn có số cuốn 2 và tổng phí nguyên vẹn.
3. Mở tệp bằng Microsoft Excel, kiểm tra đúng sáu cột, tiếng Việt, số 0 đầu của mã thẻ/điện thoại, định dạng tiền và tên `Ban-doc-qua-han-no-phi_dd-MM-yyyy.xlsx`.
4. Đăng nhập SystemAdmin tại `/AuditLog`, chọn hành động xuất, đối chiếu `Số ngày trễ tối thiểu: 7 ngày`, người xuất, thời điểm, số dòng và kết quả.
5. Thử bỏ trống, nhập 0 và ngưỡng lớn hơn dữ liệu. Người chỉ nợ xuất với trống/0; nhật ký phân biệt hai điều kiện. Không có kết quả thì hiện đúng thông báo, không tải tệp, giữ giá trị nhập.
6. Nhập `-1`, `7.5`, `abc`, `2147483648`: hiện lỗi tiếng Việt, không gửi request. Nếu gọi API bỏ qua giao diện với các giá trị đó vẫn nhận 400 JSON.
7. Có thể sửa ô khi đang chờ: nhật ký của lần đang chạy phải ghi giá trị lúc bấm xuất; ô giữ giá trị mới để dùng cho lần tiếp theo.

## Giới hạn và rủi ro

Logic tổng hợp và định dạng workbook được giữ nguyên; rủi ro thay đổi tập trung ở kiểm tra tham số và chọn dòng sau tổng hợp. Số liệu của một bạn đọc không bị thay đổi bởi ngưỡng. Thông báo rỗng được đổi theo yêu cầu Lát 2 cho cả request cũ. Các giới hạn bộ nhớ và truy vấn không giữ transaction snapshot của Lát 1 vẫn còn.

Đã triển khai phần lọc ngày trễ của AC 2 và ghi điều kiện của AC 4; cần demo trình duyệt/SQL Server/Excel để nghiệm thu thủ công. Không triển khai lọc loại thẻ hoặc kết hợp hai điều kiện.
