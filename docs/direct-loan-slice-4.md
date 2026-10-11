# Lát 4 – Quyền mượn bản sao đang giữ

## Quy tắc PO đã xác nhận

- Đúng bạn đọc được giữ có thể nhận sách tại quầy nếu thẻ, hạn mức, bản sao và đơn đều đủ điều kiện.
- Đơn giữ có hiệu lực khi gắn bản sao, thuộc nhóm `BookHoldStatus.WaitingPickupStatuses` và chưa quá `PickupDeadlineUtc`. Hạn nhận null được chấp nhận, tương thích luồng nhận sách hiện có.
- Đơn hủy, đã chuyển thành phiếu mượn hoặc quá hạn không cấp quyền giữ. Bản sao còn Đang giữ vẫn bị chặn; tra cứu không tự hủy đơn hay giải phóng bản sao.
- Một bản sao có nhiều đơn giữ hiệu lực: từ chối vì dữ liệu không nhất quán, không tự chọn đơn.
- Đơn đúng người nhưng sai đầu sách hoặc bản sao không Đang giữ: từ chối dữ liệu không nhất quán.
- Đơn chưa gắn bản sao không chiếm quyền giữ một mã vạch cụ thể. Giữ nguyên service phân bổ theo thời điểm đặt rồi ID; màn hình cho mượn không tự phân bổ.
- Khi mượn thành công: bản sao sang Đang mượn; đơn sang Đã chuyển thành phiếu mượn; phiếu lưu `SourceBookHoldId`.
- Chỉ hiển thị mã đơn `#Id` và thông báo quyền giữ; không trả tên, email, điện thoại hay hồ sơ người đặt trước khác.
- Hạn mức mỗi lượt và quy tắc xử lý hết dòng lỗi của Lát 2–3 giữ nguyên.

## File sửa

| File | Nội dung |
|---|---|
| `backend/Services/DirectLoanService.cs` | Tra cứu đơn hiệu lực kể cả khi trạng thái bản sao bị sai; so sánh người giữ; cập nhật đơn có điều kiện trong transaction; lưu SourceBookHoldId; đọc kết quả có nguồn từ đơn |
| `backend/Controllers/DirectLoanController.cs` | Truyền mã thẻ vào kiểm tra từng dòng ở tra cứu và khi trả lại trang lỗi |
| `backend/Models/DirectLoanViewModel.cs` | Thêm HoldId vào kết quả tra cứu và DTO từng dòng |
| `frontend/Views/DirectLoan/Index.cshtml` | Hiển thị mã đơn tại dòng và trên phiếu kết quả |
| `frontend/wwwroot/js/direct-loan.js` | Giữ và hiển thị mã đơn đúng dòng |
| `frontend/wwwroot/js/direct-loan-state.js` | Cập nhật/xóa metadata đơn khi sửa mã; tiếp tục bỏ phản hồi cũ |
| `backend/tests/Project.Tests/DirectLoanTests.cs` | HTTP có đơn người khác và đơn chính chủ, kiểm tra mã đơn, dữ liệu riêng tư và phiếu kết quả |
| `frontend/tests/direct-loan-state.test.cjs` | Kiểm thử metadata đơn không lẫn dòng, mất sau sửa, giữ nguyên các dòng khác khi xóa |

File mới: `backend/tests/Project.Tests/DirectLoanHoldTests.cs` (phần mở rộng fixture DirectLoanTests), tài liệu này.

Không sửa Program.cs, DbContext, entity BookHold/BookLoan, cấu hình, menu hoặc layout. Không thêm thư viện hay migration. Không commit/merge/chuyển nhánh.

## Tái sử dụng và transaction

Mở rộng trực tiếp LookupAsync, ValidateRowsAsync và ConfirmManyAsync của Lát 1–3. Tái sử dụng nhóm trạng thái đặt trước, thời hạn theo loại thẻ, lịch mở cửa, CreateForHoldAsync, audit và lịch sử bản sao.
Không gọi HoldPickupConfirmationService cho từng dòng vì service đó tự mở/commit transaction riêng, sẽ phá tính toàn vẹn của một lượt nhiều dòng. Áp dụng cùng quy tắc chuyển trạng thái và liên kết đơn trong transaction chung hiện có.

Serializable bảo vệ các truy vấn kiểm tra bản sao và đơn, gồm trường hợp đơn giữ mới xuất hiện. Khóa bạn đọc hiện có tiếp tục bảo vệ hạn mức. Trước chuyển đơn, câu cập nhật kiểm tra lại ID bản sao/đầu sách, người giữ, trạng thái chờ nhận và hạn nhận tại thời điểm cập nhật.
Chuyển bản sao có điều kiện từ Sẵn sàng hoặc Đang giữ theo kết quả kiểm tra; lịch sử ghi đúng trạng thái trước. Lỗi ở bất kỳ dòng nào rollback cả phiếu, đơn, bản sao, lịch sử và audit. Các unique constraint sẵn có trên BookCopyId và SourceBookHoldId tiếp tục chống tạo phiếu trùng.

## Kiểm thử thực tế

- Build qua `dotnet test --no-restore` thành công. Cảnh báo có sẵn: NU1900 không lấy được dữ liệu audit NuGet và giấy phép ImageSharp.
- Toàn bộ .NET: **692/692 đạt**.
- Nhóm DirectLoanTests trên SQL Server LocalDB: **73/73 đạt** sau sửa lỗi deadlock; mỗi fixture dùng database thử nghiệm riêng, tự xóa.
- JavaScript: **10/10 đạt**; cú pháp script giao diện hợp lệ.
- Kiểm thử gồm: không có đơn; đơn của người khác dù bản sao hiển thị Sẵn sàng; đúng người giữ với từng trạng thái tương thích và hạn null; đơn quá hạn/hủy/đã chuyển; nhiều đơn giữ; sai đầu sách/trạng thái; đơn chỉ gắn đầu sách; đơn xuất hiện/đổi chủ/hết hạn/hủy sau tra cứu; nhiều đơn chính chủ trong một lượt; rollback lỗi ở dòng sau; cạnh tranh chủ đơn/người khác; cạnh tranh cho mượn với phân bổ đặt trước.
- HTTP MVC/Razor xác nhận mã đơn gắn đúng RowId, giữ dòng hợp lệ, không lộ thông tin người giữ khác, và hiển thị đủ kết quả sau khi chuyển đơn chính chủ thành phiếu.
- Đã phát hiện và sửa lỗi xử lý deadlock khi EF Core bọc DbException trong InvalidOperationException. Kiểm thử ép lỗi sau dòng đầu xác nhận rollback cả đơn, phiếu, lịch sử và audit.
- Chưa nghiệm thu bố cục và thao tác bằng trình duyệt thật: kết nối Browser báo không có trình duyệt. Trạng thái JavaScript được kiểm thử độc lập; HTTP dùng ứng dụng MVC thật với database thử nghiệm riêng.

```powershell
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --no-restore
node frontend/tests/direct-loan-state.test.cjs
# Kiểm thử SQL Server trên database cách ly, tự tạo/xóa theo fixture:
$env:DIRECT_LOAN_SQLSERVER_TEST = '1'
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~DirectLoanTests
```

## Chạy và demo

Cần kết nối database dự án hoạt động, Jwt:SigningKey được cấu hình, lịch mở cửa và tài khoản Thủ thư/Quản lý/Quản trị. Từ `backend`, chạy `dotnet run --project Project.csproj --launch-profile http`, mở `/DirectLoan` tại cổng 5188.
Phiên triển khai trước đã xác định kết nối mặc định `ProjectDb` trên SQL Server `.` chưa hoạt động; Lát 4 không thay cấu hình hay tạo database người dùng. Các kiểm thử tự tạo dữ liệu cách ly.

**Tình huống người khác được giữ:**
1. Bạn đọc A còn được mượn ít nhất 2 sách; chuẩn bị hai bản sao Sẵn sàng và một bản sao Đang giữ cho đơn chờ nhận còn hạn của B, có BookCopyId đúng.
2. Tra cứu thẻ A, nhập ba mã. Có hai dòng hợp lệ và một dòng lỗi ghi mã đơn của B; nút xác nhận bị chặn.
3. Sửa dòng lỗi thành mã khác hoặc xóa dòng đó; hai dòng hợp lệ còn nguyên.
4. Xác nhận hai dòng. Hai phiếu được tạo, hai bản sao Đang mượn, số sách của A tăng 2. Đơn và bản sao của B không đổi.

**Tình huống đúng người giữ:**
1. Tra cứu thẻ B, nhập mã bản sao giữ cho B cùng một mã Sẵn sàng nếu còn hạn mức.
2. Dòng giữ hợp lệ, hiển thị mã đơn. Xác nhận một lần.
3. Phiếu của bản sao giữ có SourceBookHoldId, đơn chuyển thành Đã chuyển thành phiếu mượn, bản sao sang Đang mượn. Trang kết quả hiển thị cả phiếu từ đơn và phiếu thường; tải lại không tạo thêm phiếu.

## Tích hợp và đánh giá AC

Tích hợp đồng bộ DTO/controller/service, view và cả hai script. Không cần migration. Endpoint từng dòng thêm HoldId; client không được tự gửi quyền giữ để quyết định cho mượn.
Các luồng nhận sách, phân bổ, hủy và hết hạn hiện có không bị sửa. Serializable có thể khiến một yêu cầu cạnh tranh bị từ chối do deadlock; khi đó transaction rollback và người dùng tra cứu lại. Kiểm thử đã kiểm tra tính toàn vẹn, không bảo đảm mọi yêu cầu đồng thời đều thành công.
AC1–3 đạt hồi quy. AC4 đã triển khai và kiểm thử nghiệp vụ/HTTP/trạng thái JavaScript; cần demo trình duyệt với kết nối database hoạt động để nghiệm thu trực quan.
Giới hạn cũ về unique BookCopyId đối với phiếu lịch sử và trạng thái trả sách chưa lưu đầy đủ vẫn giữ nguyên, xem tài liệu Lát 1.
