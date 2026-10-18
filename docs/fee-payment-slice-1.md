# S4-02 — Lát 1: Ghi nhận thu phí và cập nhật số tiền còn nợ

## Phạm vi đã được xác nhận

- Chỉ thu các khoản trong `ReaderFees`, phân bổ theo `CreatedAtUtc`, sau đó `Id` tăng dần.
- Dùng `ReaderAccount.OutstandingBalance` làm số dư bạn đọc; phải bằng tổng `ReaderFee.RemainingAmount` trước khi thu. Lệch số dư thì chặn thu để đối soát, không tự tạo khoản bù.
- Khoản bồi thường mất/hỏng lưu riêng chưa thuộc phạm vi thu này.
- Không tự đổi trạng thái thẻ khi hết nợ; không tạo, in hoặc xuất biên lai.

## Thiết kế và tích hợp

`FeePaymentController` chỉ cho Thủ thư đang đăng nhập truy cập. POST yêu cầu anti-forgery token, xác nhận đã nhận tiền và thông tin số tiền hợp lệ. ID nhân viên lấy từ phiên `admin_refresh` qua `IAuditLogService`, không lấy từ biểu mẫu. Service kiểm tra lại vai trò và trạng thái tài khoản.

Số tiền dùng `decimal(18,2)`, lớn hơn 0, không quá dư nợ, tối đa hai chữ số thập phân. Giao diện hiển thị tiền theo `vi-VN`, thời điểm khoản phí ghi rõ UTC. Tra cứu không ghi dữ liệu; hỗ trợ họ tên, mã bạn đọc, mã thẻ, email và số điện thoại, tối đa 50 kết quả.

Mỗi giao dịch lưu số thực thu, số dư sau thu, thời điểm UTC, ID Thủ thư và các dòng phân bổ. `FeePayments.RequestId` có unique index trong cơ sở dữ liệu. Gửi lại cùng mã, bạn đọc, số tiền và Thủ thư trả về giao dịch đã có; thay đổi nội dung với mã cũ bị từ chối. Lỗi lưu giữ nguyên mã trong biểu mẫu để gửi lại, không nhận thêm tiền. Mã trống bị từ chối, không tự sinh khóa mới cho POST thiếu mã.

Service sử dụng transaction Serializable, kiểm tra các giá trị phí trước khi cập nhật và cập nhật số dư có điều kiện. Phí, số dư, giao dịch và phân bổ cùng commit hoặc rollback. Tổng phân bổ phải bằng số thực thu. Không mô phỏng thanh toán trên EF InMemory. Giao dịch cạnh tranh có thể bị từ chối do khóa/deadlock; người dùng gửi lại cùng biểu mẫu để xác minh. Unique index và cập nhật có điều kiện vẫn là bảo vệ phía server ngay cả khi JavaScript bị tắt.

## File tạo mới

| File | Chức năng |
| --- | --- |
| `backend/Models/FeePayment.cs` | Giao dịch và dòng phân bổ |
| `backend/Models/FeePaymentViewModels.cs` | Biểu mẫu, kết quả tra cứu, đối soát và kết quả thu |
| `backend/Data/FeePaymentMapping.cs` | Ánh xạ bảng, khóa ngoại Restrict và unique index |
| `backend/Services/IFeePaymentService.cs` | Hợp đồng chức năng thu phí |
| `backend/Services/FeePaymentService.cs` | Tra cứu và giao dịch thu phí |
| `backend/Controllers/FeePaymentController.cs` | GET tra cứu, POST thu tiền, quyền và CSRF |
| `backend/Migrations/20261020000000_AddFeePayments.cs` | Chỉ thêm hai bảng thanh toán và các index |
| `backend/Migrations/20261020000000_AddFeePayments.Designer.cs` | Mô hình đích của migration |
| `frontend/Views/FeePayment/Index.cshtml` | Màn hình thu phí dùng layout và Bootstrap hiện tại |
| `frontend/wwwroot/js/fee-payment.js` | Xác nhận trước khi gửi, chống nhấn liên tiếp |
| `backend/tests/Project.Tests/FeePaymentTests.cs` | Kiểm thử nghiệp vụ, rollback, cạnh tranh và metadata migration |
| `docs/fee-payment-slice-1.md` | Phạm vi, tích hợp, kiểm thử và demo |

## File cũ đã sửa trong phạm vi được duyệt

| File | Thay đổi và lý do |
| --- | --- |
| `backend/Program.cs` | Đăng ký `IFeePaymentService` trong DI |
| `backend/Data/ApplicationDbContext.cs` | Thêm hai DbSet và gọi ánh xạ thu phí |
| `backend/Migrations/ApplicationDbContextModelSnapshot.cs` | Bổ sung mô hình hai bảng thanh toán |
| `frontend/Views/Shared/_Layout.cshtml` | Thêm đúng một mục Thu phí cho Thủ thư |

Không sửa nghiệp vụ đăng nhập, sách, bạn đọc, mượn trả, miễn giảm hoặc cấu hình kết nối. Không cài thêm hay nâng phiên bản PackageReference. Các trường số dư/phí hiện có được cập nhật bởi giao dịch mới; không sửa định nghĩa bảng hiện có.

## Kiểm thử

Các test mới dùng SQLite trong bộ nhớ hoặc file tạm riêng, không dùng dữ liệu ứng dụng. Test metadata migration sử dụng provider SQL Server nhưng không mở kết nối. Bao phủ: không nợ; một/nhiều khoản phí; trả một phần/toàn bộ/nhiều lần; tiền 0/âm/vượt nợ/quá số lẻ/thiếu; ModelState lỗi dữ liệu; xác nhận và mã yêu cầu thiếu; gửi trùng và đổi nội dung mã cũ; hai yêu cầu đồng thời; lỗi INSERT giao dịch; vai trò và trạng thái nhân sự; số dư, phân bổ, miễn giảm, người thu, thời điểm thu; tra cứu không ghi; giữ nguyên trạng thái thẻ. Test HTTP chạy Kestrel với dữ liệu SQLite riêng, xác thực bằng phiên thử nghiệm thật trong bảng RefreshTokens, render Razor và gửi biểu mẫu qua HttpClient; kiểm tra CSRF, dữ liệu chữ thay số, thanh toán một phần, gửi lại POST và thanh toán toàn bộ.

Lệnh kiểm thử:

```powershell
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --filter FullyQualifiedName~FeePaymentTests
dotnet test backend/tests/Project.Tests/Project.Tests.csproj
```

Kết quả thực chạy:

- Build ứng dụng và test project thành công bằng SDK .NET 10.0.402 tạm trong `artifacts/dotnet-sdk/` (đã nằm trong gitignore), không đổi PackageReference.
- Nhóm thu phí: **17/17 đạt**, gồm test HTTP và kiểm tra mô hình/migration SQL Server.
- Bộ test hiện có trước phần thu phí, xuất từ commit `72ad3e3` sang `artifacts/baseline/` bằng `git archive`: **912 đạt, 40 lỗi / 952 test**.
- Lần hồi quy cuối với toàn bộ test mới: **929 đạt, 40 lỗi / 969 test**. Đối chiếu tên từng test lỗi bằng TRX: **0 khác biệt** so với baseline; chưa ghi nhận lỗi hồi quy mới.
- Chưa xác nhận thao tác JavaScript bằng trình duyệt hoặc cạnh tranh trên SQL Server thật. Luồng HTTP với cơ sở dữ liệu thử nghiệm đã chạy thành công.
- Restore/build báo cảnh báo từ phụ thuộc hiện có `System.Security.Cryptography.Pkcs 9.0.11`, giấy phép ImageSharp và analyzer ở test cũ. Không tự nâng thư viện hoặc sửa chức năng ngoài phạm vi.

Bằng chứng trong `artifacts/test-results/`: `fee-payment.trx`, `baseline.trx`, `regression-final.trx`, `regression-comparison.json` và các console log. File JSON chứa danh sách 40 test lỗi có sẵn để đối chiếu. Bản baseline và mọi CSDL kiểm thử tách khỏi dữ liệu ứng dụng. Không checkout/chuyển nhánh để chạy baseline.

Nếu máy chưa có lệnh `dotnet` trong PATH, có thể thay `dotnet` trong các lệnh bằng `& ./artifacts/dotnet-sdk/dotnet.exe`. SDK tạm, cache NuGet và kết quả test đều nằm trong `artifacts/`, không thuộc mã nguồn cần commit.

## Migration và demo

Migration **chưa được áp dụng** vào cơ sở dữ liệu hiện tại. Chỉ áp dụng trên CSDL demo tách biệt hoặc sau khi chủ dự án cho phép riêng. Kiểm tra lịch sử migration của nhánh tích hợp trước khi áp dụng; không đổi hoặc chạy lại các migration cũ đã áp dụng.

Sau khi CSDL demo đã sẵn sàng:

1. Chạy `dotnet run --project backend/Project.csproj` với cấu hình kết nối và JWT hiện có.
2. Đăng nhập bằng tài khoản Thủ thư, mở **Thu phí** hoặc `/FeePayment`.
3. Tìm và chọn bạn đọc có phí trong `ReaderFees`, khớp `OutstandingBalance`.
4. Nhập một phần dư nợ, đánh dấu đã nhận tiền, nhấn xác nhận và xác nhận hộp thoại.
5. Kiểm tra thông báo thành công, số dư và các dòng phí đã cập nhật.
6. Thu lần tiếp theo cho đến đủ; trạng thái thẻ giữ nguyên.
7. Gửi lại cùng POST để kiểm tra không có giao dịch mới hoặc trừ nợ lần hai.

## Rủi ro trước khi merge

- Đối chiếu thay đổi tại DI, DbContext, snapshot, thứ tự migration và menu với develop.
- Kiểm tra đồng thời thu phí với miễn giảm/phát sinh phí trên SQL Server thử nghiệm.
- Dữ liệu lịch sử có số dư lệch hoặc khoản bồi thường chưa thành `ReaderFees` sẽ bị chặn thu, cần PO/chủ dữ liệu đối soát riêng.
- Chạy kiểm thử hồi quy và demo trên môi trường cấu hình đầy đủ trước khi nghiệm thu.
