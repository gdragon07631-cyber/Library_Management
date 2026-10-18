# S4-02 — Lát 2: Thu phí một phần và cập nhật công nợ

## Phạm vi và kết quả

Tái sử dụng luồng thu phí Lát 1, các bảng `FeePayments`, `FeePaymentAllocations`, `ReaderFees` và số dư `ReaderAccount.OutstandingBalance`. Giữ quy tắc đã được PO/người dùng xác nhận: khoản phí phát sinh trước được thanh toán trước; cùng thời điểm thì theo ID tăng dần. Không thêm bảng, migration, API thanh toán, thư viện hoặc màn hình thu phí khác.

Sau mỗi lần thu, giao diện hiện dư nợ mới, trạng thái thẻ và lịch sử đầy đủ gồm mã giao dịch, số thực thu, dư nợ sau giao dịch, thời điểm UTC, người thu và các khoản phí được phân bổ. Lịch sử chỉ lấy của bạn đọc đang chọn và đọc `AsNoTracking`, không sửa giao dịch cũ.

Khi dư nợ còn lớn hơn 0, trạng thái thẻ giữ nguyên. Khi dư nợ về 0, chỉ trạng thái đúng chuỗi `Nợ phí` được đổi thành `Đang hoạt động`. Thẻ có trạng thái khác như `Bị khoá`, `Bị khóa - Nợ phí`, `Hết hạn` giữ nguyên. Không sửa ngày cấp/hết hạn, trạng thái hoặc khóa tài khoản bạn đọc; các kiểm tra hiệu lực và điều kiện mượn hiện có tiếp tục áp dụng.

Cập nhật phí, số dư, thẻ, giao dịch và phân bổ cùng nằm trong transaction Serializable. Điều kiện cập nhật kiểm tra dữ liệu đọc trước đó; thất bại sẽ rollback toàn bộ. Unique index `FeePayments.RequestId` và cơ chế trả lại giao dịch cũ vẫn được tái sử dụng. POST thiếu mã yêu cầu bị từ chối thay vì tự sinh mã mới; chỉ GET mới cấp mã mới. Lỗi cơ sở dữ liệu giữ mã trên biểu mẫu để gửi lại xác minh, không nhận thêm tiền.

## A. File tạo mới

| File | Mục đích |
| --- | --- |
| `backend/tests/Project.Tests/FeePaymentSlice2Tests.cs` | 17 ca kiểm thử Lát 2, bao gồm kịch bản HTTP và SQLite độc lập |
| `docs/fee-payment-slice-2.md` | Báo cáo, bằng chứng kiểm thử, rủi ro và hướng dẫn demo |

## B. File đã chỉnh sửa

| File | Nội dung và lý do |
| --- | --- |
| `backend/Services/FeePaymentService.cs` | Đọc trạng thái thẻ cùng công nợ; gỡ đúng trạng thái nợ khi thanh toán đủ trong transaction hiện có; bắt cả lỗi khởi tạo transaction, giữ mã yêu cầu khi báo lỗi |
| `backend/Models/FeePaymentViewModels.cs` | Bổ sung trạng thái thẻ và dữ liệu hiển thị lịch sử; không tự sinh mã khi bind POST thiếu mã |
| `backend/Controllers/FeePaymentController.cs` | Nạp lịch sử từ DbContext đã đăng ký, giới hạn theo bạn đọc; tạo mã mới cho GET |
| `frontend/Views/FeePayment/Index.cshtml` | Thêm trạng thái thẻ và bảng lịch sử vào màn hình đang có; giữ biểu mẫu thu phí và các trang khác |

Model hiển thị dùng chung được sửa trong phạm vi người dùng đã xác nhận. Không sửa `Program.cs`, `ApplicationDbContext`, model lưu trữ, layout/menu, cấu hình hoặc migration. Không sửa bài test cũ. Ở thời điểm bắt đầu thực hiện sau xác nhận, working tree đã sạch và file `FeePaymentTests.cs` của lượt trước không còn; không khôi phục hoặc ghi đè file đó.

## C. Chức năng hoàn thành

- Thu một phần/toàn bộ, từ chối số tiền âm, bằng 0 hoặc vượt dư nợ.
- Phân bổ FIFO, cập nhật số đã thu và dư nợ từng khoản, tổng nợ không âm.
- Lưu từng giao dịch và phân bổ độc lập; gửi lại mã cũ không tạo lần thu mới.
- Chỉ Thủ thư đang hoạt động được thực hiện thu phí theo cơ chế phiên hiện có.
- Giữ trạng thái nợ khi chưa đủ; gỡ đúng trạng thái nợ khi thanh toán đủ.
- Hiển thị lịch sử, trạng thái thẻ và dư nợ sau thanh toán trên cùng màn hình.
- Không tạo/in/xuất biên lai (Lát 3).

## D. Kiểm thử

Build backend và Razor thành công bằng SDK .NET 10.0.402 có sẵn tại `artifacts/dotnet-sdk/`. Không thêm hoặc nâng PackageReference.

| Nhóm kiểm thử mới | Số ca | Kết quả |
| --- | ---: | --- |
| Demo 200.000 → thu 80.000 → thu 120.000; phân bổ, dư nợ, lịch sử và replay | 1 | PASS |
| Thu toàn bộ với trạng thái nợ, khóa, khóa kèm nợ, hết hạn, đang hoạt động | 5 | PASS |
| Tiền âm, bằng 0, vượt dư nợ | 3 | PASS |
| Lỗi INSERT giao dịch hoặc UPDATE trạng thái thẻ, rollback cả dữ liệu | 2 | PASS |
| Giữ nguyên khóa tài khoản và hạn thẻ | 1 | PASS |
| Từ chối sai vai trò, nhân sự ngừng hoạt động và mã yêu cầu trống | 1 | PASS |
| Thu đủ cho bạn đọc chưa có thẻ | 1 | PASS |
| HTTP thực: phiên đăng nhập, CSRF, Razor, hai lần thu, lịch sử và trạng thái | 1 | PASS |
| Hai yêu cầu đồng thời, cùng mã/khác mã | 2 | PASS |
| **Tổng test Lát 2** | **17** | **17 PASS, 0 FAIL** |

Toàn bộ test dùng SQLite trong bộ nhớ/file tạm riêng, Kestrel cổng loopback ngẫu nhiên và phiên nhân sự thử nghiệm. Không chèn hay sửa dữ liệu ứng dụng. Luồng thu toàn bộ được kiểm tra lại trong 5 ca trạng thái và luồng HTTP.

Kết quả hồi quy và đối chiếu baseline được ghi trong `artifacts/test-results/slice2-regression.trx`, `slice2-baseline.trx` và `slice2-comparison.json`. Những lỗi cũ ngoài phạm vi không được sửa để chuyển kết quả sang PASS.

| Phiên bản | Tổng | PASS | FAIL |
| --- | ---: | ---: | ---: |
| Baseline trước Lát 2, commit `f57b9a4` | 952 | 912 | 40 |
| Sau Lát 2 | 969 | 929 | 40 |

Đối chiếu tên từng test lỗi: **0 khác biệt**, chưa ghi nhận lỗi hồi quy mới. Danh sách đầy đủ 40 lỗi có sẵn và 17 test Lát 2 nằm trong `slice2-comparison.json`.

Chưa kiểm tra thao tác JavaScript bằng trình duyệt thật hoặc cạnh tranh trên SQL Server thật. Kiểm thử đồng thời đã thực chạy trên SQLite; Razor đã build và được phục vụ qua HTTP thực. Restore/build còn cảnh báo từ phụ thuộc `System.Security.Cryptography.Pkcs 9.0.11`, giấy phép ImageSharp và analyzer ở test cũ; không thay đổi những thành phần ngoài phạm vi này.

## E. Rủi ro tích hợp

- Kiểm tra xung đột ở service, controller, view model và view khi ghép vào develop.
- Cần kiểm tra cạnh tranh thu phí với miễn giảm/phát sinh phí trên SQL Server thử nghiệm. Deadlock có thể khiến một yêu cầu bị từ chối; gửi lại cùng mã để kiểm tra.
- Trạng thái thẻ hiện là một chuỗi, không có cờ nợ độc lập: chỉ gỡ chuỗi `Nợ phí` đúng như đã xác nhận, không tự diễn giải chuỗi khóa kết hợp.
- Dữ liệu có tổng phí lệch `OutstandingBalance` tiếp tục bị chặn thu như Lát 1; khoản bồi thường lưu riêng chưa được tự đưa vào công nợ.
- Lịch sử lấy toàn bộ giao dịch của bạn đọc đã chọn; chưa thêm phân trang trong lát này.

## F. Demo

Frontend là Razor và tài nguyên tĩnh do backend phục vụ; không có lệnh chạy frontend riêng.

Chạy web với cấu hình SQL Server/JWT sẵn có của dự án:

```powershell
dotnet run --project backend/Project.csproj
```

CSDL demo cần đã có các bảng Lát 1. Lượt triển khai này không áp dụng migration hoặc thay đổi dữ liệu thật.

1. Đăng nhập bằng Thủ thư; mở **Thu phí** hoặc `/FeePayment`.
2. Chọn bạn đọc trong CSDL demo có tổng `ReaderFees.RemainingAmount` khớp dư nợ 200.000đ và thẻ `Nợ phí`.
3. Nhập **80.000đ**, xác nhận đã nhận tiền và xác nhận thu. Dư nợ còn **120.000đ**, thẻ vẫn `Nợ phí`, lịch sử có lần thu đầu.
4. Nhập **120.000đ** và xác nhận. Dư nợ bằng **0đ**, thẻ thành `Đang hoạt động`, lịch sử hiện cả hai giao dịch.
5. Trước khi thu hết, thử tiền âm/0/vượt nợ: không có giao dịch hoặc thay đổi số dư/trạng thái thẻ.

Nếu chưa có dữ liệu demo trong CSDL web, chạy kịch bản HTTP tự tạo dữ liệu thử nghiệm tách biệt theo cơ chế test hiện có; không chèn thủ công vào dữ liệu thật:

```powershell
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --filter FullyQualifiedName~FeePaymentSlice2Tests.HttpDemo
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --filter FullyQualifiedName~FeePaymentSlice2Tests
```

Máy chưa có `dotnet` trong PATH có thể thay bằng `& ./artifacts/dotnet-sdk/dotnet.exe`. SDK, cache NuGet, bản baseline và kết quả kiểm thử nằm trong thư mục `artifacts/` đã được gitignore.

## G. Git

Nhánh lúc kiểm tra cuối: `feature/s4-02/Thu_phi_mot_phan_va_cap_nhat_cong_no`.

```text
 M backend/Controllers/FeePaymentController.cs
 M backend/Models/FeePaymentViewModels.cs
 M backend/Services/FeePaymentService.cs
 M frontend/Views/FeePayment/Index.cshtml
?? backend/tests/Project.Tests/FeePaymentSlice2Tests.cs
?? docs/fee-payment-slice-2.md
```

Không thực hiện commit, push, pull, merge, rebase, reset, checkout, switch hoặc tạo nhánh. Bản baseline được xuất bằng `git archive` để kiểm tra riêng, không thay đổi nhánh làm việc.
