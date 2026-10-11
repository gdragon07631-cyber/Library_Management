# Lát 3 – Lỗi riêng từng dòng mượn

## Quy tắc đã xác nhận

- Giữ dòng lỗi để sửa hoặc xóa; không tự bỏ qua dòng lỗi để cho mượn các dòng khác.
- Chỉ xác nhận khi có ít nhất một dòng, mọi dòng kiểm tra xong, không có lỗi/trùng mã, bạn đọc đủ điều kiện và đủ hạn mức.
- Bản sao đổi trạng thái trước xác nhận: trả lỗi theo RowId, giữ các dòng còn lại, không lưu một phần.
- Hạn mức mỗi lượt tiếp tục theo số sách bạn đọc còn được mượn, không có giới hạn riêng.
- Bản sao Đang giữ chỉ hiển thị lỗi trạng thái; không tra cứu danh tính hoặc quyền của người đặt trước.

## File thay đổi

| File | Nội dung |
|---|---|
| `backend/Models/DirectLoanViewModel.cs` | RowId, Error, IsValid trên dòng; RowIds trong request; Rows trong kết quả thất bại |
| `backend/Services/DirectLoanService.cs` | Thông báo cho từng trạng thái; ValidateRowsAsync chỉ đọc; kiểm tra mọi dòng trong transaction trước ghi |
| `backend/Controllers/DirectLoanController.cs` | LookupCopy trả kết quả có RowId; giữ lỗi và định danh khi POST thất bại |
| `frontend/Views/DirectLoan/Index.cshtml` | Cột kiểm tra, lỗi đúng dòng, các số thống kê và nạp script trạng thái |
| `frontend/wwwroot/js/direct-loan.js` | Thêm ngay dòng đang kiểm tra, sửa tại dòng, xóa, hiển thị thống kê, khóa xác nhận |
| `backend/tests/Project.Tests/DirectLoanTests.cs` | Kiểm thử lỗi từng trạng thái, danh sách xen kẽ, RowId, sửa/xóa lỗi, HTTP trả lỗi đúng dòng và hồi quy |

File mới:
- `frontend/wwwroot/js/direct-loan-state.js`: trạng thái tạm thời từng dòng; phiên bản tra cứu để bỏ phản hồi cũ sau sửa/xóa.
- `frontend/tests/direct-loan-state.test.cjs`: 8 kiểm thử trạng thái JavaScript, không cần thư viện ngoài.
- `docs/direct-loan-slice-3.md`: tài liệu này.

Không sửa Program.cs, DbContext, entity dữ liệu dùng chung, cấu hình, layout, menu hay phân quyền. Không migration, commit hoặc merge.

## Tái sử dụng và toàn vẹn dữ liệu

Tái sử dụng LookupAsync, điều kiện bạn đọc, hạn mức theo loại thẻ, thời hạn mượn, lịch mở cửa, tạo BookLoan, audit và lịch sử trạng thái từ Lát 1–2.
ConfirmAsync một bản sao vẫn gọi cùng luồng ConfirmManyAsync. Request cũ chỉ có CopyCodes vẫn được hỗ trợ; UI mới gửi thêm RowIds tương ứng.
Backend tự kiểm tra mã và trạng thái thực trong database, không tin trạng thái Hợp lệ do client khai báo.
Các dòng sai đều được kiểm tra và trả về trước khi ghi. Transaction Serializable, khóa bạn đọc SQL Server, cập nhật bản sao có điều kiện và rollback toàn bộ được giữ nguyên.
Các lỗi về thẻ/hạn mức hoặc lỗi lưu hệ thống vẫn là thông báo chung; lỗi mã vạch và bản sao gắn từng dòng.

## Kiểm thử thực tế

- Build qua `dotnet test --no-restore`: thành công. Có cảnh báo NU1900 do truy cập audit NuGet và giấy phép ImageSharp như trước.
- Toàn bộ bộ kiểm thử .NET: **665/665 đạt**.
- Nhóm DirectLoanTests: **46/46 đạt trên SQLite**.
- Nhóm DirectLoanTests trên SQL Server LocalDB: **46/46 đạt**, gồm rollback, cạnh tranh bản sao/hạn mức và trả lỗi đúng dòng.
- JavaScript: **8/8 đạt**; xác minh kết quả đến sai thứ tự, sửa nhiều lần, phản hồi sau khi xóa, trùng mã, xóa lỗi, thống kê và điều kiện xác nhận.
- HTTP MVC/Razor: trả lỗi đúng RowId khi bản sao đổi sang sửa chữa, giữ các dòng còn lại, không tạo phiếu dở; sửa dữ liệu hợp lệ rồi xác nhận thành công; kiểm tra antiforgery, phân quyền và tải lại kết quả.
- Chưa kiểm thử bố cục/focus bằng trình duyệt thật: Browser không có kết nối trong phiên triển khai.

Lệnh:

```powershell
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --no-restore
node frontend/tests/direct-loan-state.test.cjs
# SQL Server LocalDB, database fixture riêng được tạo/xóa tự động:
$env:DIRECT_LOAN_SQLSERVER_TEST = '1'
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~DirectLoanTests
```

## Chạy và demo

Cần kết nối SQL Server có schema/dữ liệu của dự án, Jwt:SigningKey đã cấu hình và tài khoản Thủ thư/Quản lý/Quản trị.
Trong phiên này, máy chủ cũ đã dừng để build; cấu hình mặc định `ProjectDb` trên SQL Server `.` không kết nối được, và không có `ProjectDb` trên LocalDB. Không tự tạo/migrate hay đổi kết nối cơ sở dữ liệu của người dùng.

Sau khi có kết nối phù hợp, từ `backend` chạy `dotnet run --project Project.csproj --launch-profile http`, mở `http://localhost:5188/DirectLoan`.

1. Tra cứu thẻ đủ điều kiện, còn hạn mức ít nhất 3 sách.
2. Nhập 3 mã bản sao Sẵn sàng, 1 mã không tồn tại, 1 mã bản sao Đang mượn. Dùng dữ liệu demo riêng, không đổi trạng thái dữ liệu thật chỉ để thử.
3. Có 5 dòng, 3 hợp lệ và 2 lỗi đúng dòng; nút xác nhận bị khóa.
4. Sửa một dòng lỗi bằng nút Sửa, nhập mã rồi Enter hoặc Kiểm tra lại. RowId không đổi, các dòng khác giữ nguyên.
5. Thử sửa thành mã vẫn sai hoặc trùng: lỗi còn tại dòng. Xóa các dòng lỗi: thống kê và STT cập nhật, dòng hợp lệ giữ nguyên.
6. Khi còn toàn dòng hợp lệ, xác nhận một lần; mỗi bản sao có một BookLoan và tất cả đổi sang Đang mượn.
7. Để thử phản hồi chậm: sửa/xóa dòng trong lúc Đang kiểm tra; phản hồi cũ không khôi phục hoặc ghi đè dòng.

Fixture kiểm thử đã tạo kịch bản 5 dòng cách ly để kiểm chứng mà không làm hỏng dữ liệu hiện có.

## Tích hợp và đánh giá

Lấy đồng bộ controller/service/model/view và cả hai file JavaScript; không chỉ lấy script giao diện. Giao diện LookupCopy nay trả JSON có lỗi từng dòng kể cả mã không tồn tại; đây là thay đổi có chủ đích của endpoint màn hình này.
AC1 và AC2 đạt kiểm thử hồi quy. AC3 đã triển khai và đạt kiểm thử backend/HTTP/trạng thái JavaScript; nghiệm thu trực quan cần hoàn tất demo trên trình duyệt có kết nối database.
Giới hạn có sẵn về unique BookCopyId trên phiếu lịch sử và trạng thái trả sách chưa lưu đầy đủ vẫn giữ nguyên (xem tài liệu Lát 1).
