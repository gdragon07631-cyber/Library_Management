# Môi trường staging

## Phạm vi

Staging chạy ứng dụng ASP.NET Core MVC trên .NET 10 cùng SQL Server 2022 bằng Docker Compose. Khi `ASPNETCORE_ENVIRONMENT=Staging`, ứng dụng áp dụng các EF Core migrations hiện có trước khi nhận request. Cấu hình này không tự tạo tài khoản, dữ liệu mẫu hay kịch bản demo.

## A. Yêu cầu

- Docker Engine hoặc Docker Desktop đang chạy.
- Docker Compose v2 (`docker compose`).
- Kết nối Internet trong lần build image đầu tiên để tải image .NET/SQL Server và các package NuGet.
- Máy x86-64 có đủ tài nguyên cho SQL Server Developer Edition; cần xác nhận giới hạn tài nguyên cụ thể theo máy chủ nghiệm thu.

## B. Chuẩn bị cấu hình

Chạy từ thư mục gốc repository:

```powershell
Copy-Item .env.example .env
```

Mở `.env`, thay các placeholder trước khi khởi chạy. `.env` đã được Git bỏ qua. Không dùng các giá trị mẫu bên dưới cho staging có thể truy cập từ mạng.

| Biến | Bắt buộc | Thành phần | Mục đích / giá trị |
|---|---|---|---|
| `MSSQL_SA_PASSWORD` | Có | SQL Server và ứng dụng | Mật khẩu tài khoản SQL `sa`; dùng mật khẩu riêng, đáp ứng chính sách SQL Server và không chứa dấu `;` để tránh làm hỏng connection string. |
| `JWT_SIGNING_KEY` | Có | Ứng dụng | Khóa ký JWT, tối thiểu 32 byte UTF-8. Tạo bằng `openssl rand -base64 48` hoặc secret manager tương đương. |
| `EMAIL_SMTP_HOST` | Không* | Ứng dụng | Máy chủ SMTP. Để trống nếu chưa cấu hình email. |
| `EMAIL_SMTP_PORT` | Không* | Ứng dụng | Cổng SMTP; mặc định `587`. |
| `EMAIL_ENABLE_SSL` | Không* | Ứng dụng | Bật TLS cho SMTP; mặc định `true`. |
| `EMAIL_FROM` | Không* | Ứng dụng | Địa chỉ người gửi; cần khai báo cùng SMTP để gửi email. |
| `EMAIL_USERNAME` | Không* | Ứng dụng | Tài khoản SMTP nếu nhà cung cấp yêu cầu. |
| `EMAIL_PASSWORD` | Không* | Ứng dụng | Mật khẩu SMTP; lưu riêng trong `.env` hoặc secret manager. |
| `STAGING_HTTP_PORT` | Không | Compose | Cổng host cho giao diện; mặc định `5188`. |
| `STAGING_SQL_PORT` | Không | Compose | Cổng host SQL Server; mặc định `1433`. |

`*` Các biến SMTP không bắt buộc để khởi động. Những tính năng cần gửi email sẽ không hoạt động cho tới khi cấu hình SMTP đầy đủ. Địa chỉ SMTP, người gửi và thông tin xác thực cần PO/chủ hạ tầng xác nhận.

## C. Khởi chạy

Tại thư mục gốc, sau bước chuẩn bị `.env`, dùng một lệnh:

```powershell
docker compose -f docker-compose.staging.yml up --build -d
```

Lệnh build image ứng dụng và khởi chạy SQL Server cùng ứng dụng. Compose đợi SQL Server healthy trước khi khởi chạy web; ứng dụng migrate DB lúc khởi động. Lần đầu có thể mất vài phút.

Kiểm tra trạng thái và readiness:

```powershell
docker compose -f docker-compose.staging.yml ps
Invoke-WebRequest http://localhost:5188/health/ready
```

Endpoint readiness trả HTTP 200 khi ứng dụng có thể kết nối DB, nếu không sẽ là HTTP 503. Health check của service `web` kiểm tra endpoint này; service `database` có health check riêng bằng `sqlcmd`.

Xem log:

```powershell
docker compose -f docker-compose.staging.yml logs --follow
docker compose -f docker-compose.staging.yml logs --follow web
docker compose -f docker-compose.staging.yml logs --follow database
```

### Nạp dữ liệu mẫu nghiệm thu (S4-10 Lát 2)

Sau khi staging đã khởi động healthy, tại thư mục gốc chạy:

    docker compose -f docker-compose.staging.yml exec -T web dotnet Project.dll --seed-staging-demo

Lệnh chỉ chạy khi ứng dụng có ASPNETCORE_ENVIRONMENT=Staging, áp dụng migrations trước, rồi ghi dữ liệu vào SQL Server staging. Seeder tái sử dụng kho/kệ/danh mục demo hiện có và thêm tối thiểu 200 đầu sách, 500 bản sao, 50 bạn đọc, 100 phiếu mượn. Dữ liệu dùng mã STG-DEMO-*, email miền .test, và không tạo tài khoản nhân viên/quản trị. Mật khẩu ngẫu nhiên của bạn đọc chỉ lưu dưới dạng hash và không in ra log.

Seeder không chạy tự động cùng ứng dụng. Có thể chạy lại mà không nhân bản các bản ghi staging hiện có; lệnh này không phải chức năng reset dữ liệu.

Phân bố phiếu seed hiện tại: 20 đang mượn, 20 quá hạn chưa trả, 20 đã trả đúng hạn, 20 đã trả trễ hạn, 10 đóng do mất và 10 đóng do hỏng nặng. Quá hạn được xác định từ hạn trả theo ngày nghiệp vụ Việt Nam; mất/hỏng dùng các giá trị CloseReason và IncidentType có sẵn trong ứng dụng.

Sau khi chạy, terminal in tổng số bản ghi và phân bố trạng thái. Có thể kiểm tra số lượng:

    docker compose -f docker-compose.staging.yml exec -T database /bin/sh -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d ProjectDb -Q "SELECT COUNT(*) AS Books FROM Books; SELECT COUNT(*) AS Copies FROM BookCopies; SELECT COUNT(*) AS Readers FROM ReaderAccounts; SELECT COUNT(*) AS Loans FROM BookLoans;"'

Mở giao diện ở http://localhost:5188 và tra cứu các mã bạn đọc STG-DEMO-RD-* hoặc bản sao STG-DEMO-CPY-*. Các phiếu sự cố mẫu có thể xem từ màn hình lịch sử sự cố sách; dữ liệu lưu trong bảng nghiệp vụ thật, không phải dữ liệu tĩnh trên giao diện.

## D. Truy cập

Mặc định giao diện ở [http://localhost:5188](http://localhost:5188). Nếu đổi `STAGING_HTTP_PORT`, thay cổng trong URL. Các cổng được bind vào loopback để chỉ máy chạy Docker truy cập trực tiếp; DB không bị mở ra mạng ngoài. Muốn nghiệm thu từ máy khác, hạ tầng cần cấu hình reverse proxy/firewall và bind cổng web vào interface phù hợp; không nên công khai cổng SQL. DNS/TLS và địa chỉ máy chủ do hạ tầng cung cấp. Compose này không tạo địa chỉ công khai hoặc TLS.

## E. Dừng staging

Dừng container, giữ dữ liệu SQL, uploaded files và Data Protection keys:

```powershell
docker compose -f docker-compose.staging.yml down
```

Không dùng `down --volumes` trừ khi chủ động muốn xóa toàn bộ DB staging, ảnh tải lên và khóa Data Protection; thao tác đó làm mất dữ liệu không thể khôi phục từ các volume này.

## F. Lỗi thường gặp

- **Docker chưa chạy:** mở Docker Desktop hoặc khởi động Docker Engine, rồi thử lại lệnh Compose.
- **Cổng host bị chiếm:** đổi `STAGING_HTTP_PORT` hoặc `STAGING_SQL_PORT` trong `.env`; cổng trong container giữ nguyên.
- **Thiếu biến:** Compose báo biến bắt buộc chưa được đặt; kiểm tra `.env` ở thư mục gốc và tên biến.
- **Mật khẩu SQL không hợp lệ:** dùng mật khẩu mạnh theo yêu cầu SQL Server, không dùng giá trị placeholder; tránh dấu `;` trong mật khẩu vì nó được đưa vào connection string.
- **DB chưa sẵn sàng hoặc migration lỗi:** kiểm tra `docker compose ... ps` và log `database`, `web`. Web chỉ chạy migration sau khi SQL health check thành công.
- **Container đang chạy nhưng chưa sẵn sàng:** đợi health check hoàn tất và gọi `/health/ready`; trạng thái `starting`/`unhealthy` không có nghĩa hệ thống đã nhận request thành công.
- **Không truy cập được giao diện:** xác nhận cổng host, trạng thái health của `web`, log web và quy tắc firewall. Địa chỉ `localhost` chỉ dùng trên chính máy đang chạy Compose.
- **Email không gửi:** cung cấp SMTP host, port, TLS, sender và thông tin xác thực hợp lệ; email không cần thiết để khởi động web.
