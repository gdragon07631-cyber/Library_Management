# Cho mượn trực tiếp một bản sao tại quầy

## Mở và demo

1. Chạy dự án từ thư mục `backend`: `dotnet run --project Project.csproj`.
2. Đăng nhập bằng tài khoản Thủ thư, Quản lý thư viện hoặc Quản trị hệ thống.
3. Mở `/DirectLoan`. Quản lý/quản trị cũng có thể mở **Phiếu mượn → Cho mượn trực tiếp tại quầy**.
4. Nhập mã thẻ có sẵn, chọn **Tra cứu thẻ**; kiểm tra tên, loại thẻ, số sách đang mượn và số còn được mượn.
5. Nhập một mã vạch bản sao **Sẵn sàng**, chọn **Tra cứu bản sao**.
6. Chọn **Xác nhận mượn**. Kết quả hiển thị mã phiếu, ngày mượn, hạn trả, trạng thái và số sách cập nhật.
7. Phiếu cũng xuất hiện trong danh sách phiếu mượn hiện có. Xác nhận lại cùng bản sao bị từ chối.

Không cần migration mới. Không tự tạo bạn đọc/bản sao hoặc thay dữ liệu thật để demo.
Loại thẻ cần có `LoanDays > 0`; lịch mở cửa cần được cấu hình đầy đủ.

## Quy tắc tái sử dụng

- Giới hạn từ `LibraryCardType.MaxBooks`: thẻ cán bộ/giảng viên 10, loại khác 5 theo code hiện tại.
- Ngày mượn theo quy ước `DateTime.Today` đang dùng trong ứng dụng.
- Hạn trả gốc = ngày mượn + `LibraryCardType.LoanDays`, sau đó dùng `AdjustLoanDueDateAsync` để chuyển sang ngày mở cửa.
- Từ chối thẻ/tài khoản không hoạt động, bị khóa, hết hạn/chưa có hiệu lực, loại thẻ ngừng sử dụng/chưa cấu hình ngày mượn, bạn đọc quá hạn/nợ phí/đạt giới hạn.
- Tái sử dụng bộ kiểm tra điều kiện trong `BookLoanService.CreateForHoldAsync`; luồng tại quầy không tạo hay thay đổi đơn đặt giữ.
- Số sách đang mượn dùng cùng cách đếm `!IsReturned` của code hiện có. Thuộc tính trả sách hiện là `NotMapped`, nên dữ liệu trả sách chưa được lưu bền vững; lát này không thay đổi mô hình trả sách.

## Toàn vẹn dữ liệu và phân quyền

Controller riêng áp dụng `StaffOnly` với ba vai trò nhân sự; POST có antiforgery.
Tra cứu chỉ đọc. Khi xác nhận, service kiểm tra lại trong transaction Serializable.
SQL Server khóa cập nhật hồ sơ bạn đọc để tuần tự hóa các lượt xác nhận cùng bạn đọc.
Cập nhật bản sao có điều kiện `Status == Sẵn sàng`; ràng buộc duy nhất `BookLoan.BookCopyId` hiện có bảo vệ bổ sung.
Phiếu, trạng thái bản sao, lịch sử và nhật ký được commit cùng nhau; lỗi sẽ rollback.

Ràng buộc duy nhất trên `BookCopyId` hiện có không hỗ trợ mượn lại bản sao đã có phiếu cũ.
Màn hình báo rõ trường hợp dữ liệu này; không tự thay ràng buộc chung trong lát 1.

## Kiểm thử

```powershell
dotnet build backend/Project.csproj
dotnet test backend/tests/Project.Tests/Project.Tests.csproj
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --filter FullyQualifiedName~DirectLoanTests
```

Mặc định test mới dùng SQLite riêng, bao gồm HTTP/Razor, antiforgery, điều kiện thẻ/bản sao,
ngày trả, rollback khi lần lưu thứ hai lỗi, xác nhận lặp và request đồng thời.
Chạy cùng bộ test trên SQL Server LocalDB:

```powershell
$env:DIRECT_LOAN_SQLSERVER_TEST = '1'
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --filter FullyQualifiedName~DirectLoanTests
Remove-Item Env:DIRECT_LOAN_SQLSERVER_TEST
```

Mỗi test SQL Server tạo database `DirectLoanTest_<GUID>` riêng và xóa chính database đó khi xong.
Không chạy các test này với connection string database thư viện.

## Tích hợp

Chỉ hai file cũ được bổ sung: một đăng ký DI trong `backend/Program.cs` và một liên kết trong
`frontend/Views/Loan/Index.cshtml`. Các file khác là mới. Không thay luồng tạo phiếu cũ, menu,
DbContext, model dùng chung hoặc migration. Chưa commit/merge.
Không bao gồm nhiều mã vạch, lỗi theo dòng, xử lý đơn giữ của người khác hoặc nghiệp vụ trả sách.
