using System.Data;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed class BookIncidentService(ApplicationDbContext db) : IBookIncidentService
{
    private const long MaxPhotoBytes = 5 * 1024 * 1024; // 5 MB
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    public async Task<IReadOnlyList<OpenLoanItemViewModel>> GetOpenLoansAsync(CancellationToken cancellationToken = default)
    {
        var loans = await db.BookLoans
            .AsNoTracking()
            .Include(l => l.Book)
            .Include(l => l.BookCopy)
            .Include(l => l.ReaderAccount).ThenInclude(r => r!.LibraryCard)
            .Where(l => l.ReturnedAtUtc == null)
            .OrderByDescending(l => l.LoanDate)
            .ThenByDescending(l => l.Id)
            .ToListAsync(cancellationToken);

        return loans
            .Where(l => !l.IsReturned)
            .Select(l => new OpenLoanItemViewModel(
                l.Id,
                l.BookId,
                l.Book?.Title ?? "Chưa rõ tiêu đề",
                l.ReaderAccountId,
                l.ReaderAccount?.FullName ?? "Chưa rõ bạn đọc",
                l.ReaderAccount?.LibraryCard?.CardCode,
                l.LoanDate,
                l.DueDate,
                l.BookCopyId,
                l.BookCopy?.CopyCode,
                l.BookCopy?.Status,
                l.BookCopy?.PhysicalCondition))
            .ToList();
    }

    public async Task<IReadOnlyList<BookCopyIncidentItemViewModel>> GetCopiesForLoanAsync(long loanId, CancellationToken cancellationToken = default)
    {
        if (loanId <= 0) return [];

        var loan = await db.BookLoans
            .AsNoTracking()
            .Include(l => l.BookCopy).ThenInclude(c => c!.Shelf)
            .Include(l => l.BookCopy).ThenInclude(c => c!.Book)
            .FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken);

        if (loan == null || loan.ReturnedAtUtc != null || loan.IsReturned)
            return [];

        if (loan.BookCopyId == null || loan.BookCopy == null)
            return [];

        var copy = loan.BookCopy;
        return [
            new BookCopyIncidentItemViewModel(
                copy.Id,
                copy.CopyCode,
                copy.Shelf?.Name ?? "Chưa xếp kệ",
                copy.PhysicalCondition,
                copy.Status,
                copy.Book?.Title ?? "Chưa rõ tiêu đề")
        ];
    }

    public async Task<RecordIncidentOutcome> RecordIncidentAsync(RecordIncidentRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Kiểm tra tham số đầu vào cơ bản
        if (request.LoanId <= 0)
            return RecordIncidentOutcome.Failed("Vui lòng chọn phiếu mượn hợp lệ.");

        if (request.BookCopyId <= 0)
            return RecordIncidentOutcome.Failed("Vui lòng chọn bản sao sách hợp lệ.");

        var normalizedType = request.IncidentType?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedType) || !BookIncidentType.IsValid(normalizedType))
            return RecordIncidentOutcome.Failed("Tình trạng sự cố không hợp lệ. Chỉ chấp nhận 'Mất' hoặc 'Hỏng nặng'.");

        var normalizedDescription = request.Description?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDescription))
            return RecordIncidentOutcome.Failed("Vui lòng nhập mô tả tình trạng.");

        // 2. Kiểm tra ảnh chụp (nếu có)
        if (request.Photo != null && request.Photo.Length > 0)
        {
            if (request.Photo.Length > MaxPhotoBytes)
                return RecordIncidentOutcome.Failed("Kích thước ảnh chụp vượt quá giới hạn 5MB.");

            var ext = Path.GetExtension(request.Photo.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return RecordIncidentOutcome.Failed("Sai định dạng ảnh. Chỉ chấp nhận JPG/JPEG, PNG hoặc WEBP.");
        }

        // 3. Thực hiện giao dịch an toàn
        var isRelational = db.Database.IsRelational();
        await using var transaction = isRelational
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            // Kiểm tra phiếu mượn
            var loan = await db.BookLoans
                .FirstOrDefaultAsync(l => l.Id == request.LoanId, cancellationToken);

            if (loan == null)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return RecordIncidentOutcome.Failed("Phiếu mượn không tồn tại.");
            }

            if (loan.ReturnedAtUtc != null || loan.IsReturned)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return RecordIncidentOutcome.Failed("Phiếu mượn đã đóng hoặc đã được trả, không thể ghi nhận sự cố.");
            }

            // Kiểm tra bản sao sách
            var copy = await db.BookCopies
                .FirstOrDefaultAsync(c => c.Id == request.BookCopyId, cancellationToken);

            if (copy == null)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return RecordIncidentOutcome.Failed("Không tìm thấy bản sao sách.");
            }

            if (loan.BookCopyId != copy.Id)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return RecordIncidentOutcome.Failed("Bản sao không thuộc phiếu mượn được chọn.");
            }

            // Ngăn ghi nhận lặp đối với bản sao đã loại khỏi kho hoặc đã có incident
            if (string.Equals(copy.Status, BookCopyStatus.Removed, StringComparison.OrdinalIgnoreCase))
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return RecordIncidentOutcome.Failed("Không thể ghi nhận: Bản sao này đã bị loại khỏi kho.");
            }

            var duplicateIncident = await db.BookIncidents
                .AnyAsync(i => i.BookCopyId == copy.Id, cancellationToken);
            if (duplicateIncident)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return RecordIncidentOutcome.Failed("Không thể ghi nhận: Bản sao này đã được ghi nhận sự cố trước đó.");
            }

            // Lưu ảnh chụp nếu có
            string? savedPhotoPath = null;
            if (request.Photo != null && request.Photo.Length > 0)
            {
                var ext = Path.GetExtension(request.Photo.FileName).ToLowerInvariant();
                var fileName = $"{copy.Id}-{Guid.NewGuid():N}{ext}";
                var webRoot = FrontendPaths.WebRoot(Directory.GetCurrentDirectory());
                var dir = Path.Combine(webRoot, "uploads", "incidents");
                Directory.CreateDirectory(dir);
                var fullPath = Path.Combine(dir, fileName);

                await using (var fileStream = File.Create(fullPath))
                {
                    await request.Photo.CopyToAsync(fileStream, cancellationToken);
                }
                savedPhotoPath = $"/uploads/incidents/{fileName}";
            }

            // Cập nhật trạng thái bản sao thành "Đã loại khỏi kho"
            var originalStatus = copy.Status;
            copy.Status = BookCopyStatus.Removed;
            copy.StatusReason = $"{normalizedType}: {normalizedDescription}";

            // Nếu hỏng nặng, cập nhật thêm tình trạng vật lý
            if (string.Equals(normalizedType, BookIncidentType.Damaged, StringComparison.OrdinalIgnoreCase))
            {
                copy.PhysicalCondition = BookCopyCondition.MajorDamage;
            }

            var staffActor = string.IsNullOrWhiteSpace(request.StaffEmail) ? "Thủ thư" : request.StaffEmail.Trim();

            // Ghi nhận lịch sử đổi trạng thái bản sao
            db.BookCopyStatusHistories.Add(new BookCopyStatusHistory
            {
                BookCopyId = copy.Id,
                FromStatus = originalStatus,
                ToStatus = BookCopyStatus.Removed,
                Reason = $"{normalizedType}: {normalizedDescription}",
                ChangedBy = staffActor,
                ChangedAtUtc = DateTime.UtcNow
            });

            // Ghi nhận bản ghi sự cố
            var incident = new BookIncident
            {
                BookLoanId = loan.Id,
                BookCopyId = copy.Id,
                IncidentType = normalizedType,
                Description = normalizedDescription,
                PhotoPath = savedPhotoPath,
                ReportedBy = staffActor,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.BookIncidents.Add(incident);

            // Ghi nhật ký hệ thống (AuditLog)
            db.AuditLogs.Add(AuditLogService.Create(
                staffActor,
                "RecordBookIncident",
                $"Ghi nhận sách {normalizedType} – phiếu #{loan.Id}, bản sao {copy.CopyCode}: {normalizedDescription}",
                request.IpAddress ?? "127.0.0.1"));

            await db.SaveChangesAsync(cancellationToken);

            if (transaction != null)
                await transaction.CommitAsync(cancellationToken);

            return RecordIncidentOutcome.Success(incident, copy);
        }
        catch (Exception ex)
        {
            if (transaction != null)
                await transaction.RollbackAsync(CancellationToken.None);

            return RecordIncidentOutcome.Failed($"Lỗi khi ghi nhận sự cố: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<BookIncident>> GetRecentIncidentsAsync(int count = 20, CancellationToken cancellationToken = default)
    {
        return await db.BookIncidents
            .AsNoTracking()
            .Include(i => i.BookLoan).ThenInclude(l => l!.ReaderAccount)
            .Include(i => i.BookCopy).ThenInclude(c => c!.Book)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}

