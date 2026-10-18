# Tài khoản mẫu và kịch bản demo staging (S4-10 Lát 3)

## Phạm vi và xác nhận vai trò

Mã nguồn hiện khai báo ba vai trò nhân viên trong `AccountRoles`: `SystemAdmin`, `LibraryManager` và `Librarian`. Bạn đọc dùng `ReaderAccount` với luồng đăng nhập/cookie riêng, không phải vai trò trong bảng `AdminAccounts`. Vì chưa có biên bản xác nhận của PO trong repository, cấu hình demo dưới đây đề xuất bốn persona gồm ba vai trò nhân viên hiện có và một bạn đọc; cần PO xác nhận cách gọi “bốn vai trò” trước nghiệm thu. Không tạo vai trò mới.

## Truy cập và đăng nhập

- URL staging theo Compose mặc định: `http://localhost:5188` trên máy đang chạy Docker. Nếu hạ tầng triển khai ở máy khác, thay URL bằng địa chỉ DNS/TLS do hạ tầng cung cấp.
- Nhân viên đăng nhập tại `/Account/Login`; bạn đọc đăng nhập tại `/ReaderRegistration/Login`.
- Mật khẩu do người vận hành đặt trong `.env` trước khi chạy lệnh khởi tạo. Dùng bốn giá trị riêng, tối thiểu 12 ký tự; lấy từ secret manager hoặc người vận hành staging. Không có mật khẩu mẫu được lưu trong repository.

| Email đăng nhập | Persona / quyền | Biến mật khẩu |
|---|---|---|
| `stg-demo-admin@example.test` | Quản trị hệ thống (`SystemAdmin`) | `STAGING_DEMO_SYSTEM_ADMIN_PASSWORD` |
| `stg-demo-manager@example.test` | Quản lý thư viện (`LibraryManager`) | `STAGING_DEMO_LIBRARY_MANAGER_PASSWORD` |
| `stg-demo-librarian@example.test` | Thủ thư (`Librarian`) | `STAGING_DEMO_LIBRARIAN_PASSWORD` |
| `stg-demo-reader@example.test` | Bạn đọc (tài khoản `ReaderAccount`, không phải `AccountRoles`) | `STAGING_DEMO_READER_PASSWORD` |

Tạo file cấu hình riêng từ `.env.example`, đặt mật khẩu cho tài khoản còn thiếu rồi khởi chạy staging theo [hướng dẫn staging](staging.md). Khi `/health/ready` trả HTTP 200, chạy lệnh reset duy nhất `docker compose -f docker-compose.staging.yml exec -T web dotnet Project.dll --seed-staging-demo`. Lệnh nạp lại dữ liệu và bảo toàn bốn tài khoản. Nếu mật khẩu của tài khoản đã có không rõ, lệnh không đổi nó; dùng quy trình reset mật khẩu staging do đơn vị vận hành cấp. Không xóa tài khoản để chạy lại.

## Ma trận quyền để nghiệm thu

Quyền dưới đây căn cứ vào `[StaffOnly(...)]` ở controller và các kiểm tra vai trò hiện hữu. Đây là các kết quả cần quan sát khi chạy thử; chưa được đánh dấu PASS cho đến khi thực hiện trực tiếp trên staging.

| Tài khoản | Thao tác được phép cần kiểm tra | Thao tác bị cấm cần kiểm tra | Kết quả thực tế |
|---|---|---|---|
| `stg-demo-admin@example.test` | Mở `/PermissionMatrix`, `/StaffAccount`, `/Report`, `/Loan` | `/ReaderApproval` (chỉ `Librarian`) phải bị chặn | CHƯA THỰC HIỆN |
| `stg-demo-manager@example.test` | Mở `/Report`, `/Loan`, `/ReaderLoanHistory/Details?id={readerId}` | `/PermissionMatrix` (chỉ `SystemAdmin`) phải bị chặn | CHƯA THỰC HIỆN |
| `stg-demo-librarian@example.test` | Mở `/Book`, `/ReaderApproval`, `/ReaderLoanHistory/Details?id={readerId}` | `/Loan` (chỉ `SystemAdmin`/`LibraryManager`) phải bị chặn | CHƯA THỰC HIỆN |
| `stg-demo-reader@example.test` | Đăng nhập, mở hồ sơ `/ReaderRegistration/Profile` và danh sách giữ chỗ `/ReaderRegistration/MyHolds`; xem chi tiết sách công khai `/Book/Details/{id}` | Gọi `/ReaderLoanHistory/GetHistoryApi` với `Accept: application/json` phải trả 401; truy cập trang nhân viên phải chuyển về đăng nhập | CHƯA THỰC HIỆN |

Với endpoint API, xác nhận phản hồi 401 khi chưa có phiên phù hợp và 403 khi đã đăng nhập bằng tài khoản nhân viên sai vai trò. Có thể kiểm tra `/api/admin/permission-matrix` bằng tài khoản manager/librarian để thấy 403, hoặc không gửi cookie để thấy 401. Các trang HTML nhân viên có thể chuyển hướng về trang đăng nhập theo `StaffOnlyAttribute`; không coi việc ẩn liên kết giao diện là bằng chứng phân quyền. Đăng xuất rồi thử lại một URL/API được bảo vệ để xác nhận phiên đã bị thu hồi.

## Kịch bản demo

Các bước dưới đây dùng bản ghi giả lập `STG-DEMO-*` và có thể làm thay đổi trạng thái dữ liệu staging khi tạo phiếu/đặt giữ. Không thực hiện trên production.

1. **Mở staging và xác nhận dữ liệu sẵn sàng.** Tài khoản: chưa cần đăng nhập. Điều kiện: Compose đã khởi chạy. Mở `http://localhost:5188`, kiểm tra `/health/ready` trả HTTP 200; chạy lệnh reset+nạp một lần. Xác nhận log có số lượng sách, bản sao, bạn đọc, phiếu mượn, phân bố trạng thái và trạng thái bốn tài khoản, không có mật khẩu trong log. Có thể chạy lệnh lần nữa trước buổi demo để khôi phục bộ dữ liệu ban đầu.
2. **Kiểm tra quyền quản trị.** Tài khoản: `stg-demo-admin@example.test`. Đăng nhập ở `/Account/Login`, mở `/PermissionMatrix` và `/StaffAccount`; xác nhận trang quản trị tải được. Thử `/ReaderApproval`; xác nhận bị từ chối do vai trò. Đăng xuất trước bước tiếp theo.
3. **Kiểm tra luồng quản lý.** Tài khoản: `stg-demo-manager@example.test`. Đăng nhập ở `/Manager/Login`, mở `/Report` và `/Loan`. Lấy ID bạn đọc staging bằng `SELECT Id FROM ReaderAccounts WHERE Email = 'stg-demo-reader-001@example.test';`, rồi mở `/ReaderLoanHistory/Details?id={readerId}`; xác nhận có dữ liệu lịch sử mượn trả. Mở `/PermissionMatrix`; xác nhận không có quyền. Đăng xuất.
4. **Kiểm tra luồng thủ thư.** Tài khoản: `stg-demo-librarian@example.test`. Đăng nhập ở `/Librarian/Login`, mở `/Book` và `/ReaderApproval`; xác nhận các trang nghiệp vụ tải. Dùng cùng ID bạn đọc mẫu đã lấy ở bước 3 để mở `/ReaderLoanHistory/Details?id={readerId}`. Thử `/Loan`; xác nhận bị chặn theo chính sách. Đăng xuất.
5. **Kiểm tra luồng bạn đọc.** Tài khoản: `stg-demo-reader@example.test`. Đăng nhập ở `/ReaderRegistration/Login`, mở hồ sơ và danh sách giữ chỗ. Để mở một chi tiết sách mẫu, lấy một `Id` của sách staging bằng truy vấn SQL sau rồi truy cập `/Book/Details/{id}`:

   ```sql
   SELECT TOP (1) Id, Title FROM Books WHERE Isbn LIKE '979000000%';
   ```

   Xác nhận chi tiết sách hiển thị; nếu muốn thử đặt giữ, chọn sách có bản sao phù hợp và kiểm tra kết quả trên danh sách giữ chỗ. Thử truy cập trang nhân viên và gọi `/ReaderLoanHistory/GetHistoryApi` với header `Accept: application/json`; xác nhận không được cấp quyền. Đăng xuất.

## Xử lý lỗi đăng nhập

- Kiểm tra đúng trang đăng nhập theo loại tài khoản (nhân viên hoặc bạn đọc), chính tả email và biến mật khẩu tương ứng trong secret manager/.env cục bộ.
- Nếu log lệnh cho biết tài khoản đã tồn tại, seed không đổi mật khẩu hay vai trò. Dùng thông tin được cấp khi tài khoản đó được tạo hoặc quy trình reset mật khẩu nội bộ staging.
- Xác nhận `docker compose -f docker-compose.staging.yml ps` báo web/database healthy và `/health/ready` trả HTTP 200.
- Bạn đọc cần có trạng thái hoạt động, email xác nhận và không bị khóa; tài khoản seed được tạo với các trạng thái đó. Nếu loại thẻ hoạt động không có trong database, tài khoản vẫn được tạo nhưng không có thẻ để thử các nghiệp vụ phụ thuộc thẻ.
- Nếu lỗi phân quyền, ghi lại URL, tài khoản/role, mã HTTP hoặc chuyển hướng, rồi kiểm tra thuộc tính `StaffOnly` của controller. Không sửa role thủ công để vượt lỗi.

## Trạng thái kiểm thử lát 3

| Kiểm tra | Kết quả |
|---|---|
| Khởi tạo đủ bốn tài khoản, đúng role; không nhân bản hoặc đổi hash khi reset | CHƯA THỰC HIỆN — có test tự động `StagingDemoAccountsSeederTests` và test reset staging |
| Đăng nhập thực tế đủ bốn tài khoản và nhập sai mật khẩu | CHƯA THỰC HIỆN — cần chạy ứng dụng với SQL Server staging |
| Phân quyền allow/deny bằng trang và gọi API trực tiếp; đăng xuất | CHƯA THỰC HIỆN — cần phiên đăng nhập trên staging |
| Kịch bản demo theo thứ tự | CHƯA THỰC HIỆN — cần staging healthy |
| Staging lát 1 và dataset lát 2 | CHƯA THỰC HIỆN trong lượt triển khai này; xem quy trình tại [staging.md](staging.md) |

Tài khoản và dữ liệu trong tài liệu chỉ dùng cho staging, không dùng trên production. PO cần xác nhận persona bạn đọc có được tính là vai trò thứ tư cho nghiệm thu hay không.
