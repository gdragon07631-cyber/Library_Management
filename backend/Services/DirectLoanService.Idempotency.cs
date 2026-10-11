using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed partial class DirectLoanService
{
    private const string RetryMessage = "Không thể xác nhận lượt mượn. Chưa có dữ liệu được ghi nhận. Vui lòng kiểm tra và thử lại.";
    private static DirectLoanBatchOutcome UnknownResult() => new(false,
        "Đang xác minh kết quả lượt mượn. Chưa thể xác định kết quả; vui lòng kiểm tra lại, không tạo lượt mới.", IsUncertain: true);
    private static DirectLoanBatchOutcome ConflictResult() => new(false,
        "Định danh lượt mượn đã được dùng với nội dung hoặc người thực hiện khác. Vui lòng kiểm tra lượt trước.", RequestConflict: true);
    private static string PayloadHash(string card, IReadOnlyList<string> codes) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { card, codes }))));
    private ApplicationDbContext NewContext() => new((DbContextOptions<ApplicationDbContext>)db.GetService<IDbContextOptions>());

    // The same row/range lock is used for execution and verification. A missing row is
    // not interpreted as a rollback while another transaction may still be committing.
    private static async Task<DirectLoanOperation?> LockedOperationAsync(ApplicationDbContext context, Guid id, CancellationToken ct)
    {
        if (context.Database.IsSqlServer())
            return await context.DirectLoanOperations.FromSqlInterpolated(
                $"SELECT * FROM DirectLoanOperations WITH (UPDLOCK, HOLDLOCK) WHERE RequestId = {id}").SingleOrDefaultAsync(ct);
        // SQLite's write lock also serializes tests across separate connections.
        await context.DirectLoanOperations.Where(x => x.RequestId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PayloadHash, x => x.PayloadHash), ct);
        return await context.DirectLoanOperations.SingleOrDefaultAsync(x => x.RequestId == id, ct);
    }

    private async Task<DirectLoanBatchOutcome?> RegisterAsync(Guid id, string hash, string card, string[] codes,
        IReadOnlyList<string>? rowIds, int staffId, CancellationToken ct)
    {
        await using var context = NewContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var existing = await LockedOperationAsync(context, id, ct);
        if (existing != null && (existing.StaffId != staffId || existing.PayloadHash != hash)) return ConflictResult();
        if (existing == null)
        {
            context.DirectLoanOperations.Add(new() { RequestId = id, StaffId = staffId, PayloadHash = hash,
                CardCode = card, CopyCodesJson = JsonSerializer.Serialize(codes),
                RowIdsJson = JsonSerializer.Serialize(rowIds ?? codes.Select(_ => Guid.NewGuid().ToString("N")).ToArray()),
                CreatedAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return null;
    }

    private static async Task<DirectLoanBatchOutcome> CompletedResultAsync(ApplicationDbContext context, Guid id, CancellationToken ct)
    {
        var ids = await context.DirectLoanOperationItems.AsNoTracking().Where(x => x.RequestId == id)
            .OrderBy(x => x.Position).Select(x => x.BookLoanId).ToListAsync(ct);
        var loans = await context.BookLoans.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Include(x => x.BookCopy).Include(x => x.ReaderAccount).ToListAsync(ct);
        var receipts = ids.Select(id => loans.Single(x => x.Id == id)).ToList();
        return receipts.Count == 0 ? UnknownResult() : new(true, Loans: receipts);
    }

    public async Task<DirectLoanBatchOutcome> VerifyAsync(Guid requestId, int staffId, string? expectedHash = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // Fresh context/connection, independent of the failed transaction/request cancellation.
            await using var context = NewContext();
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, timeout.Token);
            var operation = await LockedOperationAsync(context, requestId, timeout.Token);
            if (operation != null && (operation.StaffId != staffId || (expectedHash != null && operation.PayloadHash != expectedHash)))
                return ConflictResult();
            if (operation?.CompletedAtUtc != null)
                return (await CompletedResultAsync(context, requestId, timeout.Token)) with { Operation = operation };
            return new(false, RetryMessage, Operation: operation);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không thể xác minh lượt mượn {RequestId}.", requestId);
            return UnknownResult();
        }
    }
}
