using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class FeePaymentService(ApplicationDbContext db) : IFeePaymentService
{
    public async Task<IReadOnlyList<FeePaymentReader>> SearchAsync(string? search, CancellationToken cancellationToken = default)
    {
        var term = search?.Trim();
        if (string.IsNullOrEmpty(term)) return [];
        return await db.ReaderAccounts.AsNoTracking()
            .Where(r => r.FullName.Contains(term) || r.StudentOrStaffCode.Contains(term) || r.Email.Contains(term)
                || r.PhoneNumber.Contains(term) || (r.LibraryCard != null && r.LibraryCard.CardCode.Contains(term)))
            .OrderBy(r => r.FullName).ThenBy(r => r.Id).Take(50)
            .Select(r => new FeePaymentReader(r.Id, r.FullName, r.StudentOrStaffCode, r.Email,
                r.LibraryCard == null ? null : r.LibraryCard.CardCode)).ToListAsync(cancellationToken);
    }

    public async Task<FeePaymentDebt?> GetDebtAsync(int readerId, CancellationToken cancellationToken = default)
    {
        var reader = await db.ReaderAccounts.AsNoTracking().Include(r => r.LibraryCard)
            .SingleOrDefaultAsync(r => r.Id == readerId, cancellationToken);
        if (reader is null) return null;
        var fees = await db.ReaderFees.AsNoTracking().Where(f => f.ReaderAccountId == readerId)
            .OrderBy(f => f.CreatedAtUtc).ThenBy(f => f.Id).ToListAsync(cancellationToken);
        return new(new(reader.Id, reader.FullName, reader.StudentOrStaffCode, reader.Email, reader.LibraryCard?.CardCode),
            reader.OutstandingBalance, fees) { CardStatus = reader.LibraryCard?.Status };
    }

    public async Task<FeePaymentResult> CollectAsync(CollectFeeViewModel request, int staffId, CancellationToken cancellationToken = default)
    {
        if (!await db.AdminAccounts.AsNoTracking().AnyAsync(a => a.Id == staffId && a.IsActive && a.Role == AccountRoles.Librarian, cancellationToken))
            return new(false, "Tài khoản không có quyền thu phí.");
        if (!request.Confirmed || request.RequestId == Guid.Empty || request.ReaderAccountId <= 0)
            return new(false, "Vui lòng xác nhận thông tin thu tiền hợp lệ.");
        if (request.Amount is not decimal amount || amount <= 0m || amount > 9999999999999999.99m || decimal.Round(amount, 2) != amount)
            return new(false, "Số tiền phải lớn hơn 0 và có tối đa 2 chữ số thập phân.");
        // Durable uniqueness and relational transactions are required; never emulate a payment in memory.
        if (!db.Database.IsRelational()) return new(false, "Thu phí yêu cầu cơ sở dữ liệu hỗ trợ giao dịch.");

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var existing = await db.FeePayments.AsNoTracking().SingleOrDefaultAsync(p => p.RequestId == request.RequestId, cancellationToken);
            if (existing is not null)
                return existing.ReaderAccountId == request.ReaderAccountId && existing.Amount == amount && existing.CollectedByAdminAccountId == staffId
                    ? new(true, "Giao dịch này đã được ghi nhận trước đó; không thu thêm tiền.", existing.Id, existing.BalanceAfter)
                    : new(false, "Mã yêu cầu đã được sử dụng cho thông tin thanh toán khác.");

            var debt = await GetDebtAsync(request.ReaderAccountId, cancellationToken);
            if (debt is null) return new(false, "Không tìm thấy bạn đọc.");
            if (!debt.IsConsistent) return new(false, "Số dư bạn đọc lệch tổng khoản phí. Vui lòng đối soát trước khi thu.");
            if (debt.Balance == 0m) return new(false, "Bạn đọc không có khoản nợ.");
            if (amount > debt.Balance) return new(false, "Số tiền thu không được vượt quá tổng nợ.");

            var payment = new FeePayment
            {
                RequestId = request.RequestId, ReaderAccountId = request.ReaderAccountId,
                CollectedByAdminAccountId = staffId, Amount = amount,
                BalanceAfter = debt.Balance - amount, PaidAtUtc = DateTime.UtcNow
            };
            var staff = await db.AdminAccounts.AsNoTracking().SingleAsync(a => a.Id == staffId, cancellationToken);
            var receipt = new FeeReceipt
            {
                FeePayment = payment, ReaderName = debt.Reader.FullName,
                CollectedByName = string.IsNullOrWhiteSpace(staff.FullName) ? staff.Email : staff.FullName
            };
            var remaining = amount;
            foreach (var fee in debt.Fees.Where(f => f.RemainingAmount > 0m))
            {
                if (remaining == 0m) break;
                var allocated = Math.Min(remaining, fee.RemainingAmount);
                // Compare all balance inputs so concurrent waivers cannot be overwritten.
                var updated = await db.ReaderFees.Where(f => f.Id == fee.Id && f.Amount == fee.Amount
                    && f.PaidAmount == fee.PaidAmount && f.WaivedAmount == fee.WaivedAmount
                    && f.IsPaid == fee.IsPaid && f.Status == fee.Status)
                    .ExecuteUpdateAsync(u => u.SetProperty(f => f.PaidAmount, fee.PaidAmount + allocated), cancellationToken);
                if (updated != 1) throw new DbUpdateConcurrencyException();
                var allocation = new FeePaymentAllocation { ReaderFeeId = fee.Id, Amount = allocated };
                payment.Allocations.Add(allocation);
                receipt.Lines.Add(new()
                {
                    FeePaymentAllocation = allocation,
                    Description = string.IsNullOrWhiteSpace(fee.Description) ? $"Khoản phí #{fee.Id}" : fee.Description
                });
                remaining -= allocated;
            }
            if (remaining != 0m || payment.Allocations.Sum(a => a.Amount) != amount)
                throw new DbUpdateConcurrencyException();
            var balanceUpdated = await db.ReaderAccounts.Where(r => r.Id == request.ReaderAccountId && r.OutstandingBalance == debt.Balance)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.OutstandingBalance, debt.Balance - amount), cancellationToken);
            if (balanceUpdated != 1) throw new DbUpdateConcurrencyException();
            // Only the standalone debt status may be cleared; other lock/status reasons remain intact.
            if (payment.BalanceAfter == 0m && string.Equals(debt.CardStatus, "Nợ phí", StringComparison.Ordinal))
            {
                var cardUpdated = await db.LibraryCards
                    .Where(card => card.ReaderAccountId == request.ReaderAccountId && card.Status == debt.CardStatus)
                    .ExecuteUpdateAsync(u => u.SetProperty(card => card.Status, "Đang hoạt động"), cancellationToken);
                if (cardUpdated != 1) throw new DbUpdateConcurrencyException();
            }
            db.FeePayments.Add(payment);
            db.FeeReceipts.Add(receipt);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(true, "Ghi nhận thu phí thành công.", payment.Id, payment.BalanceAfter);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return new(false, "Chưa thể xác nhận giao dịch. Hãy gửi lại biểu mẫu này để kiểm tra cùng mã yêu cầu; không nhận thêm tiền từ bạn đọc.");
        }
    }
}
