using System.Data;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class CompensationService(ApplicationDbContext db) : ICompensationService
{
    public async Task<CompensationPolicy?> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        return await db.CompensationPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == CompensationPolicy.SingletonId, cancellationToken);
    }

    public async Task<CompensationPolicyOutcome> UpdatePolicyAsync(
        UpdateCompensationPolicyRequest request,
        string staffRole,
        string staffEmail,
        CancellationToken cancellationToken = default)
    {
        // 1. Kiểm tra quyền hạn: Chỉ Quản lý hoặc Quản trị viên mới được cập nhật hệ số
        if (staffRole is not (AccountRoles.LibraryManager or AccountRoles.SystemAdmin))
        {
            return CompensationPolicyOutcome.Failed("Chỉ Quản lý thư viện hoặc Quản trị viên mới có quyền cập nhật hệ số bồi thường.");
        }

        // 2. Kiểm tra tính hợp lệ của hệ số
        if (request.DefaultRate <= 0)
        {
            return CompensationPolicyOutcome.Failed("Hệ số bồi thường chung phải lớn hơn 0.");
        }

        if (request.LostRate.HasValue && request.LostRate.Value <= 0)
        {
            return CompensationPolicyOutcome.Failed("Hệ số bồi thường khi mất sách phải lớn hơn 0.");
        }

        if (request.DamagedRate.HasValue && request.DamagedRate.Value <= 0)
        {
            return CompensationPolicyOutcome.Failed("Hệ số bồi thường khi hỏng nặng phải lớn hơn 0.");
        }

        // 3. Cập nhật hoặc khởi tạo policy
        var policy = await db.CompensationPolicies
            .SingleOrDefaultAsync(p => p.Id == CompensationPolicy.SingletonId, cancellationToken);

        if (policy is null)
        {
            policy = new CompensationPolicy
            {
                Id = CompensationPolicy.SingletonId,
                DefaultRate = request.DefaultRate,
                LostRate = request.LostRate,
                DamagedRate = request.DamagedRate,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedBy = staffEmail
            };
            db.CompensationPolicies.Add(policy);
        }
        else
        {
            policy.DefaultRate = request.DefaultRate;
            policy.LostRate = request.LostRate;
            policy.DamagedRate = request.DamagedRate;
            policy.UpdatedAtUtc = DateTime.UtcNow;
            policy.UpdatedBy = staffEmail;
        }

        db.AuditLogs.Add(AuditLogService.Create(
            staffEmail,
            "UpdateCompensationPolicy",
            $"Cập nhật hệ số bồi thường: Chung={request.DefaultRate}, Mất={request.LostRate?.ToString() ?? "mặc định"}, Hỏng={request.DamagedRate?.ToString() ?? "mặc định"}",
            "127.0.0.1"));

        await db.SaveChangesAsync(cancellationToken);
        return CompensationPolicyOutcome.Success(policy);
    }

    public async Task<CompensationOutcome> CalculateAndCreateCompensationAsync(long incidentId, CancellationToken cancellationToken = default)
    {
        if (incidentId <= 0)
            return CompensationOutcome.Failed("Mã sự cố không hợp lệ.");

        // Kiểm tra xem sự cố này đã có khoản bồi thường chưa (ngăn trùng lặp)
        var existing = await db.BookIncidentCompensations
            .Include(c => c.AdjustmentHistories)
            .FirstOrDefaultAsync(c => c.BookIncidentId == incidentId, cancellationToken);

        if (existing is not null)
            return CompensationOutcome.Failed("Sự cố này đã được tính khoản bồi thường trước đó.");

        var incident = await db.BookIncidents
            .Include(i => i.BookCopy)
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);

        if (incident is null)
            return CompensationOutcome.Failed("Không tìm thấy thông tin sự cố sách.");

        // Kiểm tra giá bìa: phải có và > 0 (không tự ý dùng 0 hay giá mặc định)
        var coverPrice = incident.BookCopy?.CoverPrice;
        if (!coverPrice.HasValue || coverPrice.Value <= 0)
        {
            return CompensationOutcome.Failed("Bản sao sách chưa có thông tin giá bìa hợp lệ nên không thể tự động tính khoản bồi thường.");
        }

        // Kiểm tra hệ số bồi thường
        var policy = await GetPolicyAsync(cancellationToken);
        if (policy is null || policy.DefaultRate <= 0)
        {
            return CompensationOutcome.Failed("Chưa thiết lập hệ số bồi thường trong hệ thống.");
        }

        var appliedRate = policy.GetRateFor(incident.IncidentType);
        if (appliedRate <= 0)
        {
            return CompensationOutcome.Failed("Hệ số bồi thường áp dụng không hợp lệ.");
        }

        // Tính tiền bồi thường ban đầu
        var initialAmount = decimal.Round(coverPrice.Value * appliedRate, 2);

        var compensation = new BookIncidentCompensation
        {
            BookIncidentId = incident.Id,
            RecordedCoverPrice = coverPrice.Value,
            AppliedRate = appliedRate,
            InitialAmount = initialAmount,
            CurrentAmount = initialAmount,
            IsAdjusted = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        db.BookIncidentCompensations.Add(compensation);
        await db.SaveChangesAsync(cancellationToken);

        return CompensationOutcome.Success(compensation);
    }

    public async Task<BookIncidentCompensation?> GetCompensationByIncidentIdAsync(long incidentId, CancellationToken cancellationToken = default)
    {
        if (incidentId <= 0) return null;

        return await db.BookIncidentCompensations
            .AsNoTracking()
            .Include(c => c.AdjustmentHistories)
            .FirstOrDefaultAsync(c => c.BookIncidentId == incidentId, cancellationToken);
    }

    public async Task<CompensationOutcome> AdjustCompensationAsync(
        AdjustCompensationRequest request,
        string staffRole,
        string staffEmail,
        CancellationToken cancellationToken = default)
    {
        // 1. Phân quyền: Thủ thư, Quản lý hoặc Quản trị viên
        if (string.IsNullOrWhiteSpace(staffRole) || !AccountRoles.IsValid(staffRole))
        {
            return CompensationOutcome.Failed("Bạn không có quyền thực hiện điều chỉnh khoản bồi thường.");
        }

        // 2. Kiểm tra tham số
        if (request.IncidentId <= 0)
        {
            return CompensationOutcome.Failed("Mã sự cố không hợp lệ.");
        }

        if (request.NewAmount < 0)
        {
            return CompensationOutcome.Failed("Số tiền bồi thường điều chỉnh không được nhỏ hơn 0.");
        }

        var normalizedReason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedReason))
        {
            return CompensationOutcome.Failed("Vui lòng nhập lý do điều chỉnh số tiền bồi thường.");
        }

        // 3. Tìm khoản bồi thường hiện tại
        var comp = await db.BookIncidentCompensations
            .Include(c => c.AdjustmentHistories)
            .FirstOrDefaultAsync(c => c.BookIncidentId == request.IncidentId, cancellationToken);

        if (comp is null)
        {
            // Kiểm tra xem incident có tồn tại không
            var incident = await db.BookIncidents
                .Include(i => i.BookCopy)
                .FirstOrDefaultAsync(i => i.Id == request.IncidentId, cancellationToken);

            if (incident is null)
            {
                return CompensationOutcome.Failed("Không tìm thấy thông tin sự cố tương ứng.");
            }

            // Trường hợp trước đó thiếu giá bìa/chưa có hệ số, nay thủ thư thiết lập số tiền ban đầu bằng điều chỉnh
            comp = new BookIncidentCompensation
            {
                BookIncidentId = incident.Id,
                RecordedCoverPrice = incident.BookCopy?.CoverPrice,
                AppliedRate = null,
                InitialAmount = 0,
                CurrentAmount = request.NewAmount,
                IsAdjusted = true,
                AdjustmentReason = normalizedReason,
                AdjustedBy = staffEmail,
                AdjustedAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.BookIncidentCompensations.Add(comp);
            await db.SaveChangesAsync(cancellationToken);

            db.CompensationAdjustmentHistories.Add(new CompensationAdjustmentHistory
            {
                BookIncidentCompensationId = comp.Id,
                FromAmount = 0,
                ToAmount = request.NewAmount,
                Reason = normalizedReason,
                AdjustedBy = staffEmail,
                AdjustedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            // Đã có bồi thường -> ghi nhận lịch sử điều chỉnh
            var fromAmount = comp.CurrentAmount;
            comp.CurrentAmount = request.NewAmount;
            comp.IsAdjusted = true;
            comp.AdjustmentReason = normalizedReason;
            comp.AdjustedBy = staffEmail;
            comp.AdjustedAtUtc = DateTime.UtcNow;

            db.CompensationAdjustmentHistories.Add(new CompensationAdjustmentHistory
            {
                BookIncidentCompensationId = comp.Id,
                FromAmount = fromAmount,
                ToAmount = request.NewAmount,
                Reason = normalizedReason,
                AdjustedBy = staffEmail,
                AdjustedAtUtc = DateTime.UtcNow
            });
        }

        db.AuditLogs.Add(AuditLogService.Create(
            staffEmail,
            "AdjustCompensation",
            $"Điều chỉnh tiền bồi thường sự cố #{request.IncidentId}: {comp.CurrentAmount:N0}đ (Lý do: {normalizedReason})",
            "127.0.0.1"));

        await db.SaveChangesAsync(cancellationToken);
        return CompensationOutcome.Success(comp);
    }
}

