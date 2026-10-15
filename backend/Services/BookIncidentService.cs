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
                copy.Book?.Title ?? "Chưa rõ tiêu đề",
                copy.CoverPrice)
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

            if (loan.ReturnedAtUtc != null || loan.IsReturned || !string.IsNullOrEmpty(loan.CloseReason))
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return RecordIncidentOutcome.Failed("Phiếu mượn đã đóng hoặc đã được trả, không thể ghi nhận sự cố.");
            }

            var nowUtc = request.IncidentDateUtc ?? DateTime.UtcNow;
            var incidentDate = DateOnly.FromDateTime(nowUtc);

            // Tính phí trễ hạn phát sinh đến ngày ghi nhận sự cố (Lát 3): 2.000 VNĐ / ngày trễ
            var overdueDays = incidentDate > loan.DueDate ? (incidentDate.DayNumber - loan.DueDate.DayNumber) : 0;
            var calculatedOverdueFee = overdueDays * BookLoan.DailyOverdueFeeRate;
            // Bảo toàn khoản phí trễ hạn đã phát sinh trước đó (không xóa, không ghi đè, không đặt lại về 0)
            var finalOverdueFee = Math.Max(loan.OverdueFee, calculatedOverdueFee);

            // Đóng phiếu mượn theo đúng lý do sự cố (Lát 3)
            loan.ReturnedAtUtc = nowUtc;
            loan.CloseReason = normalizedType;
            loan.OverdueFee = finalOverdueFee;

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
                ChangedAtUtc = nowUtc
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
                CreatedAtUtc = nowUtc
            };
            db.BookIncidents.Add(incident);

            // Tự động tính khoản bồi thường nếu có đủ giá bìa và hệ số bồi thường
            BookIncidentCompensation? compensation = null;
            string? compensationMessage = null;

            if (!copy.CoverPrice.HasValue || copy.CoverPrice.Value <= 0)
            {
                compensationMessage = "Bản sao chưa có giá bìa hợp lệ nên chưa thể tự động tính khoản bồi thường ban đầu.";
            }
            else
            {
                var policy = await db.CompensationPolicies
                    .AsNoTracking()
                    .SingleOrDefaultAsync(p => p.Id == CompensationPolicy.SingletonId, cancellationToken);

                if (policy is null || policy.DefaultRate <= 0)
                {
                    compensationMessage = "Hệ thống chưa thiết lập hệ số bồi thường nên chưa thể tự động tính khoản bồi thường ban đầu.";
                }
                else
                {
                    var appliedRate = policy.GetRateFor(normalizedType);
                    if (appliedRate > 0)
                    {
                        var initialAmount = decimal.Round(copy.CoverPrice.Value * appliedRate, 2);
                        compensation = new BookIncidentCompensation
                        {
                            BookIncident = incident,
                            RecordedCoverPrice = copy.CoverPrice.Value,
                            AppliedRate = appliedRate,
                            InitialAmount = initialAmount,
                            CurrentAmount = initialAmount,
                            IsAdjusted = false,
                            CreatedAtUtc = nowUtc
                        };
                        db.BookIncidentCompensations.Add(compensation);
                    }
                }
            }

            // Ghi nhật ký nghiệp vụ không thể sửa/xóa (Lát 4)
            // 1. Nhật ký ghi nhận sự cố sách
            db.BookIncidentAuditLogs.Add(new BookIncidentAuditLog
            {
                BookLoanId = loan.Id,
                BookIncident = incident,
                BookCopyId = copy.Id,
                OperationType = IncidentAuditOperations.RecordIncident,
                Actor = staffActor,
                OccurredAtUtc = nowUtc,
                IncidentType = normalizedType,
                Description = normalizedDescription,
                PhotoPath = savedPhotoPath,
                CopyStatusResult = BookCopyStatus.Removed,
                Summary = $"Ghi nhận sách {normalizedType}: Bản sao {copy.CopyCode} đã chuyển sang trạng thái Đã loại khỏi kho. Mô tả: {normalizedDescription}"
            });

            // 2. Nhật ký đóng phiếu mượn do sự cố và bảo toàn phí trễ hạn
            db.BookIncidentAuditLogs.Add(new BookIncidentAuditLog
            {
                BookLoanId = loan.Id,
                BookIncident = incident,
                BookCopyId = copy.Id,
                OperationType = IncidentAuditOperations.CloseLoan,
                Actor = staffActor,
                OccurredAtUtc = nowUtc,
                CloseReason = normalizedType,
                IncidentDate = incidentDate,
                OverdueFee = finalOverdueFee,
                InitialCompensationAmount = compensation?.InitialAmount,
                Summary = $"Đóng phiếu mượn #{loan.Id} do {normalizedType}. Ngày sự cố: {incidentDate:yyyy-MM-dd}. Phí trễ hạn bảo toàn: {finalOverdueFee:N0}đ."
            });

            // 3. Nhật ký tính khoản bồi thường ban đầu (nếu có đủ giá bìa và hệ số)
            if (compensation != null)
            {
                db.BookIncidentAuditLogs.Add(new BookIncidentAuditLog
                {
                    BookLoanId = loan.Id,
                    BookIncident = incident,
                    BookCopyId = copy.Id,
                    OperationType = IncidentAuditOperations.CalculateCompensation,
                    Actor = staffActor,
                    OccurredAtUtc = nowUtc,
                    RecordedCoverPrice = copy.CoverPrice,
                    AppliedRate = compensation.AppliedRate,
                    InitialCompensationAmount = compensation.InitialAmount,
                    ToAmount = compensation.InitialAmount,
                    Summary = $"Tính khoản bồi thường ban đầu: {compensation.InitialAmount:N0}đ (Giá bìa: {copy.CoverPrice:N0}đ, Hệ số: {compensation.AppliedRate})."
                });
            }

            // Ghi nhật ký hệ thống (AuditLog)
            db.AuditLogs.Add(AuditLogService.Create(
                staffActor,
                "RecordBookIncident",
                $"Ghi nhận sách {normalizedType} – đóng phiếu #{loan.Id} (Lý do: {normalizedType}, Phí trễ hạn: {finalOverdueFee:N0}đ), bản sao {copy.CopyCode}: {normalizedDescription}" +
                (compensation != null ? $"; Khoản bồi thường: {compensation.InitialAmount:N0}đ (Hệ số: {compensation.AppliedRate})" : ""),
                request.IpAddress ?? "127.0.0.1"));

            await db.SaveChangesAsync(cancellationToken);

            if (transaction != null)
                await transaction.CommitAsync(cancellationToken);

            return RecordIncidentOutcome.Success(
                incident,
                copy,
                compensation,
                compensationMessage,
                loan,
                finalOverdueFee,
                normalizedType);
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
            .Include(i => i.Compensation).ThenInclude(c => c!.AdjustmentHistories)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    public async Task<LoanIncidentHistoryViewModel?> GetLoanIncidentHistoryAsync(long loanId, CancellationToken cancellationToken = default)
    {
        if (loanId <= 0) return null;

        var loan = await db.BookLoans
            .AsNoTracking()
            .Include(l => l.Book)
            .Include(l => l.BookCopy)
            .Include(l => l.ReaderAccount)
            .FirstOrDefaultAsync(l => l.Id == loanId, cancellationToken);

        if (loan == null) return null;

        var incident = await db.BookIncidents
            .AsNoTracking()
            .Include(i => i.Compensation).ThenInclude(c => c!.AdjustmentHistories)
            .FirstOrDefaultAsync(i => i.BookLoanId == loanId, cancellationToken);

        var compensation = incident?.Compensation ?? await db.BookIncidentCompensations
            .AsNoTracking()
            .Include(c => c.AdjustmentHistories)
            .FirstOrDefaultAsync(c => c.BookIncidentId == (incident != null ? incident.Id : 0), cancellationToken);

        var logs = await db.BookIncidentAuditLogs
            .AsNoTracking()
            .Where(l => l.BookLoanId == loanId)
            .OrderByDescending(l => l.OccurredAtUtc)
            .ThenByDescending(l => l.Id)
            .ToListAsync(cancellationToken);

        var vouchers = await db.IncidentAdjustmentVouchers
            .AsNoTracking()
            .Where(v => v.BookLoanId == loanId)
            .OrderByDescending(v => v.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return new LoanIncidentHistoryViewModel
        {
            Loan = loan,
            Incident = incident,
            Compensation = compensation,
            Logs = logs,
            Vouchers = vouchers
        };
    }

    public async Task<IReadOnlyList<EligibleIncidentItemViewModel>> GetEligibleIncidentsForAdjustmentAsync(CancellationToken cancellationToken = default)
    {
        return await db.BookIncidents
            .AsNoTracking()
            .Include(i => i.BookLoan).ThenInclude(l => l!.ReaderAccount)
            .Include(i => i.BookCopy).ThenInclude(c => c!.Book)
            .Include(i => i.Compensation)
            .Where(i => !i.IsReversed)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new EligibleIncidentItemViewModel
            {
                IncidentId = i.Id,
                LoanId = i.BookLoanId,
                BookTitle = i.BookCopy != null && i.BookCopy.Book != null ? i.BookCopy.Book.Title : "N/A",
                CopyCode = i.BookCopy != null ? i.BookCopy.CopyCode : "-",
                ReaderName = i.BookLoan != null && i.BookLoan.ReaderAccount != null ? i.BookLoan.ReaderAccount.FullName : "-",
                IncidentType = i.IncidentType,
                Description = i.Description,
                OverdueFee = i.BookLoan != null ? i.BookLoan.OverdueFee : 0m,
                CompensationAmount = i.Compensation != null ? i.Compensation.CurrentAmount : null,
                CreatedAtUtc = i.CreatedAtUtc,
                ReportedBy = i.ReportedBy,
                IsReversed = i.IsReversed
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<CreateAdjustmentVoucherOutcome> CreateAndApplyAdjustmentVoucherAsync(
        CreateAdjustmentVoucherRequest request,
        CancellationToken cancellationToken = default)
    {
        // 1. Phân quyền: Chỉ Quản lý thư viện hoặc Quản trị hệ thống
        if (request.StaffRole is not (AccountRoles.LibraryManager or AccountRoles.SystemAdmin))
        {
            return CreateAdjustmentVoucherOutcome.Failed("Chỉ Quản lý thư viện hoặc Quản trị hệ thống mới có quyền lập và xác nhận bút toán điều chỉnh.");
        }

        // 2. Kiểm tra tính hợp lệ của tham số bắt buộc
        if (request.IncidentId <= 0)
        {
            return CreateAdjustmentVoucherOutcome.Failed("Mã sự cố không hợp lệ.");
        }

        var normalizedReason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedReason) || normalizedReason.Length < 5)
        {
            return CreateAdjustmentVoucherOutcome.Failed("Vui lòng nhập lý do điều chỉnh (tối thiểu 5 ký tự).");
        }

        var normalizedRef = request.ReferenceDocument?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRef) || normalizedRef.Length < 2)
        {
            return CreateAdjustmentVoucherOutcome.Failed("Vui lòng nhập tài liệu/biên bản căn cứ (tối thiểu 2 ký tự).");
        }

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var incident = await db.BookIncidents
                .Include(i => i.BookLoan).ThenInclude(l => l!.ReaderAccount)
                .Include(i => i.BookCopy).ThenInclude(c => c!.Book)
                .Include(i => i.Compensation)
                .FirstOrDefaultAsync(i => i.Id == request.IncidentId, cancellationToken);

            if (incident is null)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return CreateAdjustmentVoucherOutcome.Failed("Không tìm thấy thông tin sự cố gốc cần điều chỉnh.");
            }

            if (incident.IsReversed)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return CreateAdjustmentVoucherOutcome.Failed("Nghiệp vụ sự cố này đã được lập bút toán đảo trước đó, không thể đảo lặp lại.");
            }

            var loan = incident.BookLoan;
            var copy = incident.BookCopy;

            if (loan is null || copy is null)
            {
                if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                return CreateAdjustmentVoucherOutcome.Failed("Dữ liệu phiếu mượn hoặc bản sao liên quan không tồn tại.");
            }

            var nowUtc = DateTime.UtcNow;
            var voucherCode = $"ADJ-{nowUtc:yyyyMMdd}-{incident.Id:D4}";

            // Snapshot trước điều chỉnh
            var prevCopyStatus = copy.Status;
            var prevLoanCloseReason = loan.CloseReason;
            var prevCompensationAmount = incident.Compensation?.CurrentAmount ?? 0m;
            var prevOverdueFee = loan.OverdueFee;

            // Xác định trạng thái mới theo lựa chọn CopyAction
            string newCopyStatus;
            string? newLoanCloseReason;

            if (string.Equals(request.CopyAction, CopyReversalActions.ReturnToInventory, StringComparison.OrdinalIgnoreCase))
            {
                // Nhập lại kho sẵn sàng: Bản sao trở về Available, phiếu mượn đóng với lý do Đã trả
                newCopyStatus = BookCopyStatus.Available;
                newLoanCloseReason = BookLoanCloseReason.Returned;

                copy.Status = BookCopyStatus.Available;
                copy.StatusReason = $"Nhập lại kho sau bút toán điều chỉnh #{voucherCode}: {normalizedReason}";

                loan.ReturnedAtUtc = nowUtc;
                loan.CloseReason = BookLoanCloseReason.Returned;
                loan.IsReturned = true;
            }
            else
            {
                // Mặc định: Khôi phục đang mượn (Bản sao về OnLoan, phiếu mượn mở lại)
                newCopyStatus = BookCopyStatus.OnLoan;
                newLoanCloseReason = null;

                copy.Status = BookCopyStatus.OnLoan;
                copy.StatusReason = $"Khôi phục đang mượn sau bút toán điều chỉnh #{voucherCode}: {normalizedReason}";

                loan.ReturnedAtUtc = null;
                loan.CloseReason = null;
                loan.IsReturned = false;
            }

            // Khoản bồi thường: Điều chỉnh về 0đ do hủy sự cố nhầm
            if (incident.Compensation != null)
            {
                var comp = incident.Compensation;
                var oldAmount = comp.CurrentAmount;
                comp.CurrentAmount = 0m;
                comp.IsAdjusted = true;
                comp.AdjustmentReason = $"Hủy bồi thường theo bút toán điều chỉnh #{voucherCode}: {normalizedReason} (Căn cứ: {normalizedRef})";
                comp.AdjustedBy = request.StaffEmail;
                comp.AdjustedAtUtc = nowUtc;

                db.CompensationAdjustmentHistories.Add(new CompensationAdjustmentHistory
                {
                    BookIncidentCompensationId = comp.Id,
                    FromAmount = oldAmount,
                    ToAmount = 0m,
                    Reason = $"Hủy theo bút toán điều chỉnh #{voucherCode}: {normalizedReason}",
                    AdjustedBy = request.StaffEmail,
                    AdjustedAtUtc = nowUtc
                });
            }

            // Đánh dấu sự cố đã đảo
            incident.IsReversed = true;

            // Tạo bản ghi Bút toán điều chỉnh
            var voucher = new IncidentAdjustmentVoucher
            {
                VoucherCode = voucherCode,
                BookIncidentId = incident.Id,
                BookLoanId = loan.Id,
                BookCopyId = copy.Id,
                AdjustmentType = IncidentAdjustmentTypes.FullReversal,
                Reason = normalizedReason,
                ReferenceDocument = normalizedRef,
                CreatedBy = request.StaffEmail,
                ApprovedBy = request.StaffEmail,
                CreatedAtUtc = nowUtc,
                PreviousCopyStatus = prevCopyStatus,
                PreviousLoanCloseReason = prevLoanCloseReason,
                PreviousCompensationAmount = prevCompensationAmount,
                PreviousOverdueFee = prevOverdueFee,
                NewCopyStatus = newCopyStatus,
                NewLoanCloseReason = newLoanCloseReason,
                NewCompensationAmount = 0m,
                NewOverdueFee = prevOverdueFee,
                Status = "Đã xác nhận",
                Notes = request.Notes?.Trim() ?? string.Empty
            };
            db.IncidentAdjustmentVouchers.Add(voucher);

            // Ghi nhật ký nghiệp vụ kiểm toán bất biến (Lát 4 & Lát 5)
            db.BookIncidentAuditLogs.Add(new BookIncidentAuditLog
            {
                BookLoanId = loan.Id,
                BookIncidentId = incident.Id,
                BookCopyId = copy.Id,
                OperationType = "Bút toán điều chỉnh",
                Actor = request.StaffEmail,
                OccurredAtUtc = nowUtc,
                CloseReason = newLoanCloseReason,
                CopyStatusResult = newCopyStatus,
                FromAmount = prevCompensationAmount,
                ToAmount = 0m,
                OverdueFee = prevOverdueFee,
                AdjustmentReason = $"Lý do: {normalizedReason} | Căn cứ: {normalizedRef}",
                Summary = $"Lập bút toán điều chỉnh #{voucherCode}: Đảo ghi nhận {incident.IncidentType} bản sao {copy.CopyCode}. Trạng thái bản sao: {newCopyStatus}. Bồi thường điều chỉnh từ {prevCompensationAmount:N0}đ về 0đ. Căn cứ: {normalizedRef}."
            });

            // Ghi nhật ký hệ thống chung
            db.AuditLogs.Add(AuditLogService.Create(
                request.StaffEmail,
                "ReverseBookIncident",
                $"Bút toán #{voucherCode}: Đảo sự cố #{incident.Id} trên phiếu #{loan.Id} – Lý do: {normalizedReason} – Căn cứ: {normalizedRef}",
                "127.0.0.1"));

            await db.SaveChangesAsync(cancellationToken);

            incident.ReversalVoucherId = voucher.Id;
            await db.SaveChangesAsync(cancellationToken);

            if (transaction != null)
                await transaction.CommitAsync(cancellationToken);

            return CreateAdjustmentVoucherOutcome.Success(voucher);
        }
        catch (Exception ex)
        {
            if (transaction != null)
                await transaction.RollbackAsync(CancellationToken.None);

            return CreateAdjustmentVoucherOutcome.Failed($"Lỗi khi lập bút toán điều chỉnh: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<IncidentAdjustmentVoucher>> GetVouchersByLoanIdAsync(long loanId, CancellationToken cancellationToken = default)
    {
        if (loanId <= 0) return [];

        return await db.IncidentAdjustmentVouchers
            .AsNoTracking()
            .Where(v => v.BookLoanId == loanId)
            .OrderByDescending(v => v.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }
}

