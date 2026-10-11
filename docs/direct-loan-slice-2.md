# Lát 2 – Nhập nhiều mã vạch trong một lượt mượn

## Quy tắc đã chốt

PO xác nhận trong phiên triển khai: **Theo số sách bạn đọc còn được mượn, không có giới hạn riêng cho mỗi lượt**.
Giới hạn tổng hiện có từ `LibraryCardType.MaxBooks`: cán bộ/giảng viên 10, các loại còn lại 5.
Chọn vượt hạn mức vẫn giữ danh sách và cho xóa bớt; không cho xác nhận.

## File và phạm vi

Sửa:
- `backend/Models/DirectLoanViewModel.cs`: DTO danh sách mã vạch, dòng đang chọn, kết quả nhiều dòng.
- `backend/Services/DirectLoanService.cs`: `ConfirmManyAsync`; `ConfirmAsync` của Lát 1 gọi cùng luồng với một bản sao.
- `backend/Controllers/DirectLoanController.cs`: tra cứu JSON chỉ đọc, POST xác nhận danh sách có antiforgery, giữ danh sách khi lỗi, chuyển hướng GET hiển thị kết quả.
- `frontend/Views/DirectLoan/Index.cshtml`: bảng bản sao, tổng số chọn/còn được chọn, cảnh báo và danh sách phiếu đã tạo.
- `backend/tests/Project.Tests/DirectLoanTests.cs`: bổ sung kiểm thử nhiều bản sao, HTTP, rollback và cạnh tranh.

Tạo:
- `frontend/wwwroot/js/direct-loan.js`: nhập bằng Enter, thêm dòng, chống trùng, xóa dòng, cập nhật số lượng, focus lại ô nhập, chặn gửi lặp trong lúc xử lý.
- `docs/direct-loan-slice-2.md`: tài liệu này.

Tái sử dụng tra cứu/điều kiện mượn của Lát 1, `BookLoanService.CreateForHoldAsync`, lịch mở cửa, thời hạn theo loại thẻ, audit, lịch sử trạng thái và phân quyền hiện có.
Không thay `Program.cs`, DbContext, model dữ liệu dùng chung, cấu hình, schema, layout hoặc menu. Không có migration.

## Giao dịch

Mô hình hiện tại có mỗi `BookLoan` tương ứng một bản sao, không có phiếu cha. Một lần xác nhận tạo N dòng với N mã phiếu, cùng bạn đọc và trong cùng transaction Serializable.
Khóa cập nhật trên bạn đọc ở SQL Server giúp tuần tự hóa kiểm tra hạn mức. Các bản sao được cập nhật có điều kiện từ Sẵn sàng sang Đang mượn theo thứ tự ID.
Kiểm tra lại thẻ, số lượng, mã trùng và mọi bản sao trước khi ghi. Mỗi dòng tái sử dụng nghiệp vụ tạo phiếu có sẵn; lỗi ở bất kỳ bước nào rollback toàn bộ, kể cả audit/lịch sử.
Kết quả dùng chuyển hướng GET với các ID vừa tạo nên tải lại trang không gửi lại POST và không mất kết quả.

## Chạy và demo

Chạy từ thư mục `backend`: `dotnet run --project Project.csproj --launch-profile http`.
Cần SQL Server theo cấu hình hiện có và `Jwt:SigningKey` trong user-secrets hoặc biến môi trường `Jwt__SigningKey` (ít nhất 32 byte).
Mở `http://localhost:5188/DirectLoan`, đăng nhập Thủ thư/Quản lý thư viện/Quản trị.

1. Chuẩn bị bạn đọc có thẻ hoạt động, chưa hết hạn, không nợ/phạt/quá hạn, còn được mượn ít nhất 3 sách; loại thẻ có LoanDays và lịch mở cửa đã cấu hình.
2. Chuẩn bị 3 bản sao Sẵn sàng chưa có phiếu mượn; dùng mã thật trong cơ sở dữ liệu.
3. Nhập mã thẻ và tra cứu. Nhập mỗi mã vạch rồi Enter; mỗi lần thêm thành công ô nhập trống và nhận lại con trỏ.
4. Thử nhập mã trùng: hiện thông báo chung, không thêm dòng. Thử xóa rồi nhập lại: STT và tổng số cập nhật.
5. Nhấn Xác nhận mượn một lần: hiển thị 3 phiếu, cả 3 bản sao Đang mượn, số đang mượn tăng 3 và hạn mức còn lại giảm 3.
6. Tải lại trang kết quả: còn đủ 3 phiếu, không tạo thêm dòng.

Không tự thêm dữ liệu demo vào cơ sở dữ liệu của người dùng. Fixture kiểm thử tự tạo dữ liệu cách ly.

## Kiểm thử đã chạy

- Build qua `dotnet test --no-restore`: thành công. Còn cảnh báo sẵn có NU1900 do không lấy được dữ liệu audit NuGet và cảnh báo giấy phép ImageSharp.
- Toàn bộ `backend/tests/Project.Tests/Project.Tests.csproj`: **655/655 đạt**.
- Nhóm `DirectLoanTests` trên SQL Server LocalDB: **36/36 đạt**.
- Bao gồm 2/3/5 bản sao; thẻ cán bộ 10 bản sao không bị áp giới hạn riêng; rỗng; trùng; không tồn tại; đổi trạng thái; thẻ bị khóa; vượt hạn mức; cập nhật đủ dòng/trạng thái/audit/số lượng; rollback lỗi ở dòng thứ hai; xác nhận lặp; hai batch trùng bản sao hoặc tranh hạn mức; hồi quy Lát 1.
- Kiểm thử HTTP qua MVC/Razor thật: phân quyền, antiforgery, tra cứu không ghi DB, giữ dòng khi bị từ chối, chuyển hướng sau xác nhận, hiển thị mọi phiếu và tải lại kết quả.
- Máy chủ chạy tại cổng 5188; kiểm tra trang DirectLoan chuyển sang đăng nhập khi chưa có phiên và file JavaScript phục vụ HTTP 200.
- Cú pháp JavaScript hợp lệ. **Chưa kiểm thử thao tác JavaScript và bố cục bằng trình duyệt**, vì không có trình duyệt kết nối. Cần chạy demo mục trên để nghiệm thu thao tác Enter, focus, xóa và cảnh báo trên giao diện.

Lệnh toàn bộ: `dotnet test backend/tests/Project.Tests/Project.Tests.csproj --no-restore`.
SQL Server: đặt `$env:DIRECT_LOAN_SQLSERVER_TEST='1'`, chạy cùng lệnh với `--filter FullyQualifiedName~DirectLoanTests`.
Fixture SQL chỉ tạo/xóa cơ sở dữ liệu riêng `DirectLoanTest_<GUID>`.

## Tích hợp và giới hạn

AC1 được giữ qua cùng service và kiểm thử hồi quy. AC2 đã triển khai và kiểm thử backend/HTTP; nghiệm thu trực quan còn cần demo trên trình duyệt.
Chưa triển khai lỗi độc lập từng dòng hoặc thông báo riêng cho bản sao giữ theo đơn của bạn đọc khác (Lát 3).
Giới hạn dữ liệu có sẵn của Lát 1 vẫn còn: unique `BookLoan.BookCopyId` không cho tạo phiếu mới cho bản sao đã có phiếu lịch sử; trạng thái trả sách hiện không được lưu đầy đủ. Xem `direct-loan-slice-1.md`.
Khi tích hợp cần lấy cả file JavaScript mới và các thay đổi Razor/service/controller; không chỉ lấy giao diện. Chưa commit, merge hay chuyển nhánh.
