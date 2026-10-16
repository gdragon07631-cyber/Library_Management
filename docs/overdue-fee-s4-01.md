# S4-01 – Phí trễ hạn khi nhận trả (Lát 1 + Lát 2)

## Quyết định PO đã xác nhận (16/10/2026)

1. **Số ngày trễ tính theo lịch mở cửa** (Lát 2): đếm những ngày thư viện mở cửa trong khoảng
   từ ngày sau hạn trả đến ngày trả thực tế (gồm cả ngày trả nếu hôm đó mở cửa). Trả đúng hoặc trước hạn = 0.
2. **Làm tròn**: phí làm tròn đến bội số 1.000 VNĐ gần nhất; đúng mức giữa (x.500) làm tròn lên.
   Ví dụ 2.499 → 2.000; 2.500 → 3.000; 3.500 → 4.000.
3. **Không có mức phí mặc định**: `LoanPolicies.DailyOverdueFee` = NULL nghĩa là Quản lý chưa khai báo;
   khi đó tính thử và xác nhận nhận trả đều bị chặn với thông báo "Chưa cấu hình chính sách phí trễ hạn".
   Bản ghi chính sách hiện có giữ nguyên giá trị.
4. Phần khoản phải thu và miễn giảm đã có từ trước được giữ nguyên.

## Công thức

```
Ngày trễ  = |{ d : HạnTrả < d ≤ NgàyTrả, thư viện mở cửa ngày d }|
Phí thô   = Ngày trễ × Mức phí mỗi ngày (LoanPolicy.DailyOverdueFee)
Phí dự kiến = round_half_up(Phí thô / 1.000) × 1.000
```

Hàm dùng chung: `backend/Services/OverdueFeeCalculator.cs`, được gọi cho cả tính thử
(`GET api/loan-returns/lookup`) và xác nhận (`POST api/loan-returns/{id}/return`).

## Xác định ngày mở cửa

`WorkingScheduleService.GetConfiguredOpenDatesAsync` (dữ liệu Sprint 1):

- `WeeklyWorkingSchedules`: thứ trong tuần mở/đóng (cuối tuần đóng cửa nếu được khai báo như vậy).
- `HolidayClosures`: ngày nghỉ lễ **và** ngày đóng cửa đột xuất (cùng một bảng, phân biệt bằng `Reason`).
- Một ngày mở cửa khi thứ đó `IsOpen = true` và không nằm trong `HolidayClosures`.
- Nếu lịch tuần chưa khai báo đủ 7 ngày: báo lỗi, không tự tạo lịch, không nhận trả.

## Database

Migration `20261019000000_MakeDailyOverdueFeeNullableWithoutDefault`: cho phép NULL và bỏ default 2.000
của cột `LoanPolicies.DailyOverdueFee`. Không cập nhật dữ liệu.

> Cơ sở dữ liệu cài mới vẫn nhận giá trị 2.000 từ dữ liệu seed/migration lịch sử
> (`AddAuditLogsAndLoanPolicy`, `AddDailyOverdueFeeToLoanPolicy`). Không sửa migration đã phát hành.

