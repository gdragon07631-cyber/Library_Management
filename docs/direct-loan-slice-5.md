# Lát 5 – Giao dịch nguyên vẹn và thử lại an toàn

## Quy tắc đã chốt

- Giữ danh sách sau lỗi. Nếu đã xác minh chưa ghi nhận nghiệp vụ, cho phép thử lại cùng RequestId và cùng nội dung.
- Khi sửa danh sách hoặc mã thẻ sau thất bại đã xác minh, tạo RequestId mới. Cùng RequestId nhưng khác nội dung hoặc nhân viên bị từ chối.
- Nếu yêu cầu đã thành công, trả lại đúng các phiếu trước đó, theo thứ tự nhập; không tạo phiếu mới.
- Khi mất phản hồi hoặc chưa xác định kết quả commit, khóa sửa/xác nhận và cho phép kiểm tra kết quả. Không tự kết luận rollback.
- Thành công: “Xác nhận mượn sách thành công.”
- Lỗi hệ thống đã xác minh rollback: “Không thể xác nhận lượt mượn. Chưa có dữ liệu được ghi nhận. Vui lòng kiểm tra và thử lại.” Lỗi nghiệp vụ vẫn giữ thông báo và lỗi từng dòng của Lát 1–4.
- Giới hạn mỗi lượt vẫn bằng số sách bạn đọc còn được mượn, không thêm mức riêng.

## File và phạm vi thay đổi

| File | Loại | Nội dung |
|---|---|---|
| `backend/Models/DirectLoanOperation.cs` | Mới | Yêu cầu bền vững và liên kết đến từng BookLoan |
| `backend/Services/DirectLoanService.Idempotency.cs` | Mới | Đăng ký RequestId, khóa, đối chiếu nội dung/nhân viên, xác minh trên context mới, đọc lại phiếu theo thứ tự |
| `backend/Migrations/20261011160000_AddDirectLoanOperations.cs` | Mới | Tạo hai bảng và ràng buộc |
| `backend/Migrations/20261011160000_AddDirectLoanOperations.Designer.cs` | Mới | Target model của migration |
| `backend/tests/Project.Tests/DirectLoanAtomicityTests.cs` | Mới | Fault injection, commit không rõ kết quả, gửi trùng/đồng thời, phân quyền tra cứu, kiểm tra snapshot/script |
| `frontend/tests/direct-loan-recovery.test.cjs` | Mới | Kiểm tra event handler thực tế với DOM/network giả lập |
| `backend/Data/ApplicationDbContext.cs` | Sửa | Mapping hai bảng; khai báo rõ chỉ mục ExpiredBookCopyId đã có trong snapshot/migration cũ, tránh sinh thay đổi xóa chỉ mục ngoài phạm vi |
| `backend/Migrations/ApplicationDbContextModelSnapshot.cs` | Sửa | Snapshot hai bảng mới |
| `backend/Models/DirectLoanViewModel.cs` | Sửa | RequestId, trạng thái thử lại/xác minh và kết quả nghiệp vụ |
| `backend/Services/DirectLoanService.cs` | Sửa | Bao luồng hiện tại bằng kiểm tra idempotency; lưu marker hoàn tất/liên kết cùng transaction; xác minh sau exception |
| `backend/Controllers/DirectLoanController.cs` | Sửa | Kiểm tra RequestId, phản hồi JSON, endpoint Status và fallback HTML |
| `backend/tests/Project.Tests/DirectLoanTests.cs` | Sửa | HTTP gửi lại cùng yêu cầu; cạnh tranh bản sao/hạn mức với và không có RequestId |
| `frontend/Views/DirectLoan/Index.cshtml` | Sửa | RequestId ẩn, metadata khôi phục, nút kiểm tra kết quả |
| `frontend/wwwroot/js/direct-loan.js` | Sửa | Chặn gửi lặp, giữ danh sách trong sessionStorage, thử lại cùng ID, khôi phục sau reload, khóa khi chưa rõ kết quả |

Tái sử dụng LookupAsync, ValidateRowsAsync, BookLoanService.CreateForHoldAsync, lịch mở cửa, chính sách hạn trả, trạng thái đơn giữ, audit và lịch sử bản sao. Không thay đổi Program.cs, thư viện, cấu hình, layout, menu hoặc phân quyền hiện có.

## Ranh giới transaction

1. Một transaction ngắn đăng ký RequestId, nhân viên, SHA-256 của mã thẻ và danh sách mã vạch đã trim (có thứ tự), cùng dữ liệu khôi phục. Bản ghi đăng ký còn lại sau rollback để cùng ID không bị sử dụng với nội dung khác. Đây là metadata chống trùng, không phải phiếu mượn.
2. Transaction nghiệp vụ Serializable khóa yêu cầu, khóa bạn đọc trên SQL Server bằng UPDLOCK/HOLDLOCK, kiểm tra lại thẻ, hạn mức, các dòng và quyền giữ.
3. Tất cả BookLoan, cập nhật bản sao, đơn giữ, audit, lịch sử, liên kết yêu cầu và marker hoàn tất được ghi trong **cùng một transaction nghiệp vụ**. Chỉ commit sau dòng cuối và liên kết cuối. Trạng thái bản sao/đơn được cập nhật có điều kiện; bản sao xử lý theo ID để giảm xung đột khóa.
4. Transaction chưa commit bị rollback khi dispose; ChangeTracker được clear để lần SaveChanges sau không ghi lại dữ liệu lỗi. Số sách đang mượn được tính từ BookLoan, không có bộ đếm riêng cần cập nhật.
5. Sau exception, context/kết nối mới xác minh với timeout riêng 15 giây, không phụ thuộc token đã hủy của HTTP. Xác minh dùng cùng khóa yêu cầu nên không đọc “chưa có” trong khi transaction đó còn đang commit. Không truy cập được kết quả thì trả trạng thái chưa xác định.

Kiến trúc hiện tại có **một BookLoan cho mỗi bản sao**, không có bảng phiếu cha/chi tiết riêng. Năm bản sao tạo năm BookLoan liên kết cùng RequestId; không tự thiết kế lại model phiếu.

## Chống trùng và giao diện

PK RequestId, liên kết BookLoanId duy nhất, khóa transaction và marker hoàn tất bảo vệ ở backend. Hai lần gửi cùng ID bị tuần tự hóa; lần sau đọc lại kết quả. Hai ID khác nhau vẫn phải vượt qua khóa hạn mức, kiểm tra trạng thái, cập nhật có điều kiện và ràng buộc BookLoan hiện có.

Trình duyệt lưu RequestId, mã thẻ và các dòng trước POST. Nhấn đúp chỉ gửi một lần. Mất mạng: giữ snapshot, khóa chỉnh sửa, hiện “Kiểm tra kết quả”. Tải lại trang khôi phục snapshot và chỉ GET trạng thái; chưa tự POST lại. Đã xác minh thất bại: mở “Thử lại”. Đã thành công: chuyển đến kết quả trước đó và xóa snapshot tương ứng. Khi không thể lưu sessionStorage, không gửi yêu cầu không thể khôi phục.

Endpoint Status yêu cầu nhân viên đăng nhập và đúng nhân viên đã tạo yêu cầu. Không trả exception/stack trace/chuỗi kết nối vào JSON.

## Kết quả thực tế ngày 11/10/2026

- Build backend và test project trong `dotnet test --no-restore`: thành công, không có lỗi biên dịch. Có cảnh báo NU1900 (không lấy được dữ liệu kiểm tra lỗ hổng NuGet) và cảnh báo license ImageSharp hiện có.
- Toàn bộ suite .NET: **709/709 qua**. Sau đó thêm hai trường hợp RequestId khác nhau cạnh tranh bản sao/hạn mức; chạy nhóm tương ứng **4/4 qua** (hai cũ, hai mới). Tổng cộng **711 trường hợp riêng biệt đã qua**, không tuyên bố đã chạy lại toàn suite 711 trong một lệnh.
- SQL Server LocalDB: nhóm DirectLoan **90/90 qua**; sau khi thêm hai trường hợp, nhóm cạnh tranh **4/4 qua**. Tổng **92 trường hợp DirectLoan riêng biệt** đã qua trên SQL Server. Mỗi fixture dùng database `DirectLoanTest_<GUID>` riêng và tự xóa.
- JavaScript: **10/10** kiểm thử trạng thái dòng + **6/6** kiểm thử khôi phục qua Node runtime. Đây là kiểm thử logic/event handler, không phải kiểm thử hiển thị trên trình duyệt thật.
- Fault injection đã chạy: lỗi tạo BookLoan đầu/giữa/cuối trong 5 dòng; cập nhật bản sao đầu/giữa/cuối; cập nhật đơn giữ; lưu liên kết yêu cầu; exception trước commit và sau commit thành công; mất khả năng xác minh sau commit rồi kết nối lại. Sau rollback truy vấn DB bằng context mới kiểm tra không có loan/link/history/audit, giữ trạng thái bản sao/đơn và số sách đang mượn.
- Đã kiểm tra thành công 2/3/5 dòng, replay qua context mới, ID khác nội dung, hai lần cùng ID đồng thời, giới hạn và bản sao cạnh tranh, quyền đọc trạng thái; giữ các test Lát 1–4 và HTTP/Razor thật.
- Snapshot khớp model SQL Server. Sinh script migration bằng IMigrator thành công, chỉ tạo hai bảng mới, không ALTER BookLoans hoặc DROP TABLE.
- Chưa chạy giao diện bằng trình duyệt thật. Chưa áp dụng migration vào database dùng chung; chưa chạy migration nâng cấp dữ liệu trên bản sao database production. SQL integration dùng EnsureCreated trên DB tạm.

## Chạy kiểm thử và demo

```powershell
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --no-restore
# SQL Server LocalDB: chỉ dùng fixture DB tạm, không dùng database của ứng dụng.
$env:DIRECT_LOAN_SQLSERVER_TEST='1'
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~DirectLoanTests
Remove-Item Env:DIRECT_LOAN_SQLSERVER_TEST
# Khi Node có trên PATH:
node frontend/tests/direct-loan-state.test.cjs
node frontend/tests/direct-loan-recovery.test.cjs
```

Demo tự động, không cần dữ liệu người dùng:

- `IdempotentReplayReturnsOriginalReceiptsAndRejectsDifferentPayload`: tạo 2/3/5 phiếu, gửi lại cùng ID, kiểm tra đúng ID phiếu cũ.
- `EveryWriteFaultRollsBackAndSameRequestCanRetry`: lỗi dòng thứ ba trong 5 dòng, xác minh DB không ghi nghiệp vụ, tắt lỗi, thử lại thành công cùng ID.
- `CommitFailureIsVerifiedBeforeReportingAnOutcome`: phân biệt lỗi trước commit và mất phản hồi sau commit.
- `UnknownCommitDoesNotClaimRollbackAndLaterVerificationFindsReceipts`: không xác định được commit, khôi phục kết nối rồi tìm lại đúng phiếu.

Dùng `dotnet test ... --filter FullyQualifiedName~<tên test>` để demo từng nhóm. Fault injection chỉ nằm trong test, không có nút giả lập lỗi production.

Demo UI cần SQL Server hoạt động và database demo đã có migration mới sau review. Công cụ `dotnet-ef` hiện chưa cài trong môi trường này; không tự cài hoặc tự update database dùng chung. Nhóm tích hợp cần review migration rồi áp dụng trên database demo riêng bằng quy trình EF hiện có. Sau đó:

```powershell
dotnet run --project backend/Project.csproj --launch-profile http
```

Mở `http://localhost:5188/DirectLoan`, đăng nhập Thủ thư, tra mã thẻ còn ít nhất 5 lượt, nhập 5 bản sao hợp lệ, xác nhận. Kiểm tra 5 dòng kết quả, các trạng thái và hạn mức mới. Demo mất phản hồi bằng môi trường test/network có kiểm soát; kiểm tra kết quả trước khi thử lại.

## Lưu ý review và tích hợp

- Cần triển khai migration trước khi sử dụng luồng mới; chưa áp dụng vào database của người dùng. FK Restrict giữ lịch sử RequestId/phiếu/nhân viên; không xóa bản ghi idempotency khi còn cần chống replay.
- sessionStorage giữ thông tin trong tab; đóng tab/xóa storage làm mất snapshot phía trình duyệt. Backend vẫn giữ trạng thái và chống trùng nếu còn RequestId. Chưa bổ sung màn hình tìm tất cả yêu cầu theo nhân viên.
- Serializable có thể gây chờ khóa/deadlock. Nhánh lỗi xác minh kết quả trước khi cho thử lại; không tự commit một phần. Với lỗi hạ tầng, có thể trả thông báo toàn lượt thay vì lỗi từng dòng; lần thử lại vẫn kiểm tra lại toàn bộ điều kiện.
- Ràng buộc BookLoan.BookCopyId duy nhất từ các lát trước vẫn giữ nguyên, kể cả phiếu đã trả; vấn đề mượn lại bản sao có lịch sử nằm ngoài Lát 5.
- Đã thấy snapshot cũ lệch chỉ mục ExpiredBookCopyId; chỉ đồng bộ khai báo model để giữ nguyên schema đã có, không thêm migration thay đổi chức năng hết hạn giữ sách.
- Không thực hiện commit, merge hoặc chuyển nhánh trong công việc này. Những thay đổi được commit từ bên ngoài trong khi làm việc được giữ nguyên.

AC1–AC4 tiếp tục qua kiểm thử hồi quy. AC5 có bằng chứng transaction, rollback, idempotency và khôi phục trên SQLite/SQL Server. Code sẵn sàng review; xác nhận demo UI và nâng cấp database đích vẫn là bước tích hợp chưa thực hiện.
