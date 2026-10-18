using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class FeeReceiptService(ApplicationDbContext db)
{
    public async Task<FeeReceiptResult> GetAsync(long paymentId, CancellationToken cancellationToken = default)
    {
        var snapshot = await db.FeeReceipts.AsNoTracking().Include(r => r.FeePayment)
            .Include(r => r.Lines).ThenInclude(l => l.FeePaymentAllocation)
            .SingleOrDefaultAsync(r => r.FeePaymentId == paymentId, cancellationToken);
        if (snapshot is null)
        {
            var exists = await db.FeePayments.AsNoTracking().AnyAsync(p => p.Id == paymentId, cancellationToken);
            return new(null, exists, exists
                ? "Giao dịch cũ chưa có bản chụp biên lai. Không đủ dữ liệu để tái hiện thông tin tại thời điểm thanh toán."
                : "Không tìm thấy giao dịch thanh toán.");
        }
        var payment = snapshot.FeePayment!;
        var lines = snapshot.Lines.OrderBy(l => l.FeePaymentAllocationId).Select(l =>
            new FeeReceiptLineViewModel(l.FeePaymentAllocation!.ReaderFeeId, l.Description, l.FeePaymentAllocation.Amount)).ToList();
        if (payment.Amount <= 0m || lines.Count == 0 || lines.Any(l => l.Amount <= 0m)
            || lines.Sum(l => l.Amount) != payment.Amount
            || snapshot.Lines.Any(l => l.FeePaymentAllocation!.FeePaymentId != paymentId))
            return new(null, true, "Dữ liệu biên lai không khớp giao dịch. Vui lòng liên hệ người quản lý để đối soát.");
        return new(new(payment.Id, payment.ReaderAccountId, payment.PaidAtUtc,
            snapshot.ReaderName, snapshot.CollectedByName, payment.Amount, lines), true);
    }
}
