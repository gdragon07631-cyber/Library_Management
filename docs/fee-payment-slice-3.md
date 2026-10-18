# S4-02 — Lát 3: Tạo và in biên lai thu phí

## Quyết định đã được xác nhận

- Tạo bản chụp biên lai cùng transaction ghi nhận thanh toán thành công; không dựng lại tên hoặc mô tả từ dữ liệu hiện tại khi in.
- Mã biên lai là `BL-` + ID giao dịch, tối thiểu 8 chữ số, không cắt ID lớn hơn 8 chữ số. Không đổi ID giao dịch hoặc sinh mã mới khi xem/in lại.
- Mẫu A4, giờ thu tiền UTC+7, họ tên bạn đọc/Thủ thư, bảng các khoản thực thu và tổng giao dịch; in bằng trình duyệt, có thể lưu PDF nếu môi trường hỗ trợ.
- Giao dịch cũ chưa có bản chụp: hiển thị thông báo thiếu dữ liệu lịch sử, không backfill hoặc giả lập tên tại thời điểm thanh toán.
- Được bổ sung cấu trúc lưu trữ và sửa các file tích hợp cần thiết. Migration mới **chưa áp dụng vào CSDL hiện tại**.

## A. File tạo mới

| File | Mục đích |
| --- | --- |
| `backend/Models/FeeReceipt.cs` | Bản chụp tên bạn đọc, người thu và mô tả các khoản phí |
| `backend/Models/FeeReceiptViewModel.cs` | Nội dung biên lai, mã cố định và chuyển giờ UTC+7 |
| `backend/Data/FeeReceiptMapping.cs` | Ánh xạ khóa chính/khóa ngoại cho bản chụp |
| `backend/Services/FeeReceiptService.cs` | Đọc biên lai bằng AsNoTracking, kiểm tra tổng và liên kết giao dịch |
| `backend/Controllers/FeeReceiptController.cs` | GET xem biên lai dành cho Thủ thư |
| `backend/Migrations/20261021000000_AddFeeReceipts.cs` | Chỉ thêm hai bảng snapshot, không sửa hoặc backfill dữ liệu cũ |
| `backend/Migrations/20261021000000_AddFeeReceipts.Designer.cs` | Mô hình đích của migration |
| `frontend/Views/FeeReceipt/Details.cshtml` | Trang xem trước và nút in, độc lập với layout quản trị |
| `frontend/Views/FeeReceipt/Unavailable.cshtml` | Thông báo giao dịch không tồn tại hoặc thiếu dữ liệu biên lai |
| `frontend/wwwroot/css/fee-receipt.css` | Giao diện xem trước, A4, lặp tiêu đề bảng, ẩn thanh thao tác khi in |
| `backend/tests/Project.Tests/FeeReceiptTests.cs` | 12 test nghiệp vụ, HTTP, rollback và metadata migration |
| `docs/fee-payment-slice-3.md` | Báo cáo và hướng dẫn demo |

## B. File đã chỉnh sửa

| File | Thay đổi, lý do và phạm vi ảnh hưởng |
| --- | --- |
| `backend/Data/ApplicationDbContext.cs` | Thêm DbSet snapshot và gọi mapping mới; không sửa mapping nghiệp vụ cũ |
| `backend/Migrations/ApplicationDbContextModelSnapshot.cs` | Ghi nhận hai bảng snapshot mới để EF quản lý migration |
| `backend/Program.cs` | Đăng ký service chỉ đọc biên lai |
| `backend/Services/FeePaymentService.cs` | Chụp tên/mô tả và lưu cùng lần SaveChanges trong transaction thanh toán hiện có; giữ kiểm tra, phân bổ, số dư và cập nhật thẻ của Lát 1–2 |
| `frontend/Views/FeePayment/Index.cshtml` | Thêm liên kết Xem biên lai ở từng dòng lịch sử, gồm giao dịch vừa thu thành công |

Không sửa model thanh toán hiện có, controller thanh toán, thuật toán phân bổ, phân quyền chung, layout/menu, cấu hình kết nối, PackageReference hoặc các bài test cũ. Các thay đổi Lát 2 có sẵn được giữ nguyên. Trong lúc triển khai, chủ workspace đã ghi nhận Lát 2 ở commit `8545548`; agent không thực hiện commit/chuyển nhánh.

## C. Chức năng hoàn thành

- Một biên lai cho mỗi giao dịch mới thành công, cả thu toàn bộ và thu một phần. `FeeReceipts.FeePaymentId` vừa là khóa chính duy nhất vừa là khóa ngoại đến giao dịch.
- Mỗi dòng snapshot gắn với một dòng phân bổ duy nhất qua khóa chính/khóa ngoại `FeePaymentAllocationId`.
- Chỉ chụp dữ liệu chưa được lưu cố định: tên bạn đọc, tên người thu, mô tả khoản phí. Số tiền, thời điểm và dòng thực thu lấy từ lịch sử giao dịch/phân bổ đã lưu, không tính lại từ giá khoản phí hiện tại.
- Xem tại `/FeeReceipt/Details?paymentId=<ID>`; chỉ vai trò Thủ thư đang có phiên hợp lệ được truy cập theo `StaffOnly` và `IAuditLogService` hiện có.
- Tổng các dòng biên lai phải bằng số thực thu; dữ liệu không khớp bị từ chối hiển thị để đối soát.
- Giao dịch không tồn tại trả HTTP 404 cùng thông báo. Giao dịch cũ thiếu snapshot trả HTTP 409 cùng thông báo, không ghi dữ liệu khi đọc.
- Xem trước, in và in lại dùng cùng mã/nội dung. GET không gọi service thu tiền, không cập nhật số dư, trạng thái thẻ hoặc lịch sử.
- Nút In biên lai gọi `window.print()` như cách in nhãn sách hiện có. Không tạo API xuất PDF hoặc thêm thư viện tài liệu.

## D. Kiểm thử thực chạy

Build backend và Razor thành công bằng SDK .NET 10.0.402 có sẵn trong `artifacts/dotnet-sdk/`.

| Test biên lai | Số ca | Kết quả |
| --- | ---: | --- |
| `SuccessfulPayment_CreatesOneStableReceiptWithActualAllocations` — một phần/toàn bộ; mã, giờ, tên, từng dòng, tổng và replay | 2 | PASS |
| `SnapshotNamesAndDescriptions_SurviveLaterDataChanges` — sửa tên bạn đọc, người thu, mô tả và số tiền gốc rồi đọc bằng DbContext mới | 1 | PASS |
| `InvalidPayment_CreatesNoReceipt` — tiền âm/0/vượt nợ | 3 | PASS |
| `ReceiptWriteFailure_RollsBackPaymentDebtAndCard` — lỗi INSERT snapshot hoặc dòng snapshot, kiểm tra rollback và gửi lại | 2 | PASS |
| `ReadingAndReprinting_DoesNotChangeDebtCardOrHistory` — hai lần thu 80.000đ/120.000đ và nhiều lần đọc lại | 1 | PASS |
| `MissingPaymentAndLegacyPayment_AreNotFabricated` | 1 | PASS |
| `MigrationMatchesModelAndOnlyAddsSnapshotTables` — metadata SQL Server, không kết nối CSDL | 1 | PASS |
| `HttpReceipt_PartialFullReprintPermissionsAndMissingPayment` — HTML thực, liên kết lịch sử, in lại, 404, 401/403 cho người không có quyền | 1 | PASS |
| **Tổng** | **12** | **12 PASS, 0 FAIL** |

17/17 test Lát 2 cũng PASS, gồm thu một phần/toàn bộ, cập nhật thẻ, lỗi lưu và cạnh tranh thanh toán. Test dùng SQLite và nhân sự/bạn đọc giả lập riêng; không dùng dữ liệu thật.

| Hồi quy | Tổng | PASS | FAIL |
| --- | ---: | ---: | ---: |
| Trước Lát 3 (`slice2-regression.trx`) | 969 | 929 | 40 |
| Sau Lát 3 (`slice3-regression.trx`) | 981 | 941 | 40 |

Đối chiếu tên từng test lỗi: **0 khác biệt**, chưa ghi nhận lỗi hồi quy mới. Không sửa 40 lỗi cũ ngoài phạm vi. Bằng chứng trong `artifacts/test-results/slice3-targeted.trx`, `slice3-regression.trx`, `slice3-comparison.json` và console log. Build vẫn có cảnh báo từ phụ thuộc/analyzer cũ; không nâng thư viện hoặc sửa các phần ngoài nhiệm vụ.

### Kiểm tra bản in

Chrome headless dùng profile tạm, dữ liệu tổng hợp từ test HTTP và CSS thật của ứng dụng để in PDF. Không sử dụng profile người dùng hoặc gửi lệnh đến máy in vật lý.

| Bản in thử | Trang | Kiểm tra |
| --- | ---: | --- |
| Giao dịch 80.000đ | 1 | Đủ tên/mã/giờ/chi tiết/tổng, tiếng Việt đúng dấu |
| Giao dịch 120.000đ sau lần thu đầu | 1 | Đúng hai dòng phân bổ 40.000đ + 80.000đ, tổng 120.000đ |
| Mẫu stress 75 dòng dài, chỉ phục vụ QA | 7 | Đủ 75 dòng, không tràn ngang; tiêu đề bảng lặp lại, tổng ở trang cuối |

Đã render cả 9 trang PDF thành PNG bằng bộ render PDF có sẵn của Windows và kiểm tra trực quan từng trang: không phát hiện chữ mất dấu, chồng lấn, cắt dòng hoặc mất tổng tiền. Thanh thao tác được ẩn khi in. Artifacts nằm tại `artifacts/receipt-qa/`; không thuộc mã nguồn hoặc biên lai thực tế.

Chưa kiểm thử in giấy qua phần cứng/driver máy in hoặc cạnh tranh trên SQL Server thật. Hộp thoại in của từng trình duyệt có thể có tùy chọn header/footer và scale riêng.

## E. Rủi ro và tích hợp

- `Program.cs`, DbContext, model snapshot và phần lưu thanh toán là các điểm có thể xung đột khi tích hợp develop; cần đối chiếu các thay đổi của đồng đội.
- Migration `20261021000000_AddFeeReceipts` chỉ thêm `FeeReceipts` và `FeeReceiptLines`. **Chưa chạy migration vào CSDL hiện tại.** Chỉ chạy trên CSDL demo được phép hoặc sau khi có xác nhận riêng.
- CSDL chạy phiên bản ứng dụng mới cần có hai bảng snapshot trước khi thu tiền mới. Nếu chưa có bảng hoặc ghi snapshot lỗi, transaction bị rollback; không ghi khoản thu thiếu biên lai.
- Các giao dịch cũ không có snapshot sẽ không in được biên lai gốc chính xác; đây là chính sách đã được người dùng duyệt, không tự lấy tên hiện tại để bù dữ liệu lịch sử.
- Liên kết đến giao dịch và phân bổ dùng Restrict, không cascade xóa biên lai. Không cung cấp chức năng chỉnh sửa bản chụp.
- Chi tiết tiền/thời gian đọc từ giao dịch đã ghi nhận. Không hỗ trợ chỉnh sửa thủ công lịch sử giao dịch/phân bổ trong lát này.

## F. Chạy và demo

Frontend là Razor và tài nguyên tĩnh do backend phục vụ, không có lệnh chạy frontend riêng:

```powershell
dotnet run --project backend/Project.csproj
```

Dùng URL localhost do ứng dụng in ra khi chạy, sau đó mở `/FeePayment`. Cần cấu hình SQL Server/JWT hiện có và CSDL demo được phép đã sẵn sàng với migration Lát 1–3; lượt này không đổi cấu hình, không áp dụng migration hoặc chèn dữ liệu thật.

1. Đăng nhập bằng Thủ thư. Chọn bạn đọc thử nghiệm nợ 200.000đ, các khoản phí khớp tổng nợ.
2. Thu 80.000đ theo luồng hiện có. Dư nợ còn 120.000đ, thẻ vẫn `Nợ phí`.
3. Tại lịch sử, nhấn **Xem biên lai** của giao dịch 80.000đ, kiểm tra mã/tên/giờ/các khoản thực thu. Nhấn **In biên lai**, chọn máy in hoặc lưu PDF nếu trình duyệt hỗ trợ.
4. Quay lại và thu 120.000đ. Dư nợ bằng 0, trạng thái thẻ nợ được gỡ theo Lát 2. Mở biên lai riêng của giao dịch mới.
5. Mở lại biên lai 80.000đ từ lịch sử, in lại: mã và nội dung giữ nguyên, không phát sinh giao dịch hoặc giảm nợ lần nữa.
6. Dùng tài khoản không phải Thủ thư hoặc chưa đăng nhập để truy cập URL biên lai: bị từ chối theo cơ chế phân quyền hiện có.
7. Mở mã giao dịch không tồn tại hoặc giao dịch cũ thiếu snapshot để kiểm tra thông báo phù hợp.

Kịch bản tự động bằng dữ liệu tách biệt:

```powershell
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --filter FullyQualifiedName~FeeReceiptTests
dotnet test backend/tests/Project.Tests/Project.Tests.csproj --filter FullyQualifiedName~FeePaymentSlice2Tests
```

Nếu chưa có `dotnet` trong PATH, thay bằng `& ./artifacts/dotnet-sdk/dotnet.exe`. Không cài thêm thư viện để triển khai hoặc kiểm tra chức năng này.

## G. Git

Nhánh khi kiểm tra: `feature/s4-02/tao_va_in_bien_lai_thu_phi`.

5 file cũ chỉnh sửa và 12 file mới liệt kê ở mục A/B đều chưa được agent commit. Các mục mới trong `git status` gồm `backend/Controllers/FeeReceiptController.cs`, `backend/Data/FeeReceiptMapping.cs`, hai file migration, hai model biên lai, service, test, hai view, CSS và tài liệu này.

Không thực hiện commit, push, pull, merge, rebase, reset, checkout, switch hoặc tạo nhánh. SDK, file kiểm thử/PDF/PNG và profile Chrome chỉ nằm trong `artifacts/` đã được gitignore.
