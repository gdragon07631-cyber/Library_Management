using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Project.Data;
using Project.Models;

namespace Project.Services;

public sealed record StagingAcceptanceSeedResult(
    int Books,
    int Copies,
    int Readers,
    int Loans,
    IReadOnlyDictionary<string, int> LoanStates,
    IReadOnlyDictionary<string, bool> DemoAccounts);

/// <summary>Builds a clearly marked, persistent staging dataset using the application's real entities.</summary>
public sealed class StagingAcceptanceDataSeeder(
    ApplicationDbContext db,
    IPasswordHasher<AdminAccount> staffPasswordHasher,
    IPasswordHasher<ReaderAccount> readerPasswordHasher)
{
    private const int BookTarget = 200;
    private const int CopyTarget = 500;
    private const int ReaderTarget = 50;
    private const int LoanTarget = 100;
    private const string ReaderCodePrefix = "STG-DEMO-RD-";
    private const string CopyCodePrefix = "STG-DEMO-CPY-";

    private static readonly string[] Authors =
    [
        "Nguyễn Nhật Ánh", "Nam Cao", "Tô Hoài", "Vũ Trọng Phụng", "Nguyễn Du",
        "J. K. Rowling", "George Orwell", "Antoine de Saint-Exupéry", "Dale Carnegie", "Robert C. Martin"
    ];

    private static readonly string[] Categories =
    [
        "Văn học Việt Nam", "Văn học nước ngoài", "Thiếu nhi", "Khoa học và công nghệ",
        "Lập trình và công nghệ thông tin", "Kỹ năng sống", "Lịch sử", "Kinh tế và quản trị",
        "Giáo dục", "Tâm lý học"
    ];

    private static readonly string[] FirstNames =
    ["An", "Bình", "Chi", "Dũng", "Giang", "Hà", "Khánh", "Linh", "Minh", "Ngọc"];
    private static readonly string[] LastNames =
    ["Nguyễn", "Trần", "Lê", "Phạm", "Hoàng"];
    private static readonly string[] Titles =
    ["Khám phá", "Cẩm nang", "Nhập môn", "Thực hành", "Góc nhìn", "Tri thức", "Hành trình"];
    private static readonly string[] Publishers =
    ["NXB Tri Thức", "NXB Giáo Dục", "NXB Tổng hợp", "NXB Văn hóa", "NXB Khoa học"];

    public async Task<StagingAcceptanceSeedResult> SeedAsync(
        IReadOnlyDictionary<string, string?> accountPasswords,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ResetSampleDataAsync(cancellationToken);

        // Reuse the existing catalog seeder inside this reset transaction.
        await new LibraryDemoDataSeeder(db).SeedAsync(cancellationToken);
        await EnsureSupplementalBooksAndCopiesAsync(cancellationToken);
        await EnsureReadersAndLoansAsync(cancellationToken);
        var demoAccounts = await new StagingDemoAccountsSeeder(db, staffPasswordHasher, readerPasswordHasher)
            .SeedAsync(accountPasswords, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var bookIsbns = Enumerable.Range(1, BookTarget).Select(CreateSampleIsbn).ToArray();
        var books = await db.Books.CountAsync(book => book.Isbn != null && bookIsbns.Contains(book.Isbn), cancellationToken);
        var distinctBookIsbns = await db.Books.Where(book => book.Isbn != null && bookIsbns.Contains(book.Isbn))
            .Select(book => book.Isbn).Distinct().CountAsync(cancellationToken);
        var demoBookIds = await db.Books.Where(book => book.Isbn != null && bookIsbns.Contains(book.Isbn))
            .Select(book => book.Id).ToArrayAsync(cancellationToken);
        var copies = await db.BookCopies.CountAsync(copy => demoBookIds.Contains(copy.BookId), cancellationToken);
        var distinctCopyCodes = await db.BookCopies.Where(copy => demoBookIds.Contains(copy.BookId))
            .Select(copy => copy.CopyCode).Distinct().CountAsync(cancellationToken);
        if (books != BookTarget || distinctBookIsbns != BookTarget || copies != CopyTarget || distinctCopyCodes != CopyTarget)
            throw new InvalidOperationException(
                $"Dữ liệu staging không đúng bộ chuẩn: sách {books}/{BookTarget}, bản sao {copies}/{CopyTarget}.");
        var readers = await db.ReaderAccounts.CountAsync(reader => reader.StudentOrStaffCode.StartsWith(ReaderCodePrefix), cancellationToken);
        var distinctReaderCodes = await db.ReaderAccounts.Where(reader => reader.StudentOrStaffCode.StartsWith(ReaderCodePrefix))
            .Select(reader => reader.StudentOrStaffCode).Distinct().CountAsync(cancellationToken);
        var demoCopyIds = await db.BookCopies.Where(copy => demoBookIds.Contains(copy.BookId))
            .Select(copy => copy.Id).ToListAsync(cancellationToken);
        var loans = await db.BookLoans.AsNoTracking()
            .Where(loan => loan.BookCopyId.HasValue && demoCopyIds.Contains(loan.BookCopyId.Value))
            .Include(loan => loan.BookCopy)
            .ToListAsync(cancellationToken);
        if (readers != ReaderTarget || distinctReaderCodes != ReaderTarget || loans.Count != LoanTarget)
            throw new InvalidOperationException(
                $"Dữ liệu staging không đúng bộ chuẩn: bạn đọc {readers}/{ReaderTarget}, phiếu mượn {loans.Count}/{LoanTarget}.");
        var demoReaderCodes = Enumerable.Range(1, ReaderTarget).Select(index => $"{ReaderCodePrefix}{index:000}").ToArray();
        var hasInvalidLoanRelationship = loans.Any(loan => loan.BookCopy is null || loan.BookCopy.BookId != loan.BookId) ||
            await db.BookLoans.AnyAsync(loan => demoCopyIds.Contains(loan.BookCopyId ?? 0) &&
                (!demoReaderCodes.Contains(loan.ReaderAccount!.StudentOrStaffCode) || !demoBookIds.Contains(loan.BookId)), cancellationToken);
        var hasInvalidCopyRelationship = await db.BookCopies.AnyAsync(copy => demoBookIds.Contains(copy.BookId) &&
            (copy.Shelf == null || copy.Book == null), cancellationToken);
        var hasInvalidBookRelationship = await db.Books.AnyAsync(book => demoBookIds.Contains(book.Id) &&
            (book.Author == null || book.Category == null), cancellationToken);
        if (hasInvalidLoanRelationship || hasInvalidCopyRelationship || hasInvalidBookRelationship)
            throw new InvalidOperationException("Dữ liệu staging có quan hệ phiếu mượn/bản sao/đầu sách không hợp lệ.");

        var today = LibraryCalendarDate.Today();
        var states = loans.GroupBy(loan => GetState(loan, today))
            .ToDictionary(group => group.Key, group => group.Count());
        var expectedStates = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Đang mượn"] = 20,
            ["Quá hạn chưa trả"] = 20,
            ["Đã trả đúng hạn"] = 20,
            ["Đã trả trễ hạn"] = 20,
            ["Đóng do mất"] = 10,
            ["Đóng do hỏng nặng"] = 10
        };
        if (expectedStates.Any(pair => !states.TryGetValue(pair.Key, out var count) || count != pair.Value) ||
            states.Values.Sum() != LoanTarget)
            throw new InvalidOperationException("Phân bố trạng thái phiếu staging không đúng bộ chuẩn.");

        await transaction.CommitAsync(cancellationToken);

        return new(books, copies, readers, loans.Count, states, demoAccounts.Created);
    }

    private async Task ResetSampleDataAsync(CancellationToken cancellationToken)
    {
        var sampleIsbns = Enumerable.Range(1, BookTarget).Select(CreateSampleIsbn).ToArray();
        var markedCopyBookIds = db.BookCopies.Where(copy => copy.CopyCode.StartsWith("DEMO-CPY-") ||
                copy.CopyCode.StartsWith(CopyCodePrefix))
            .Select(copy => copy.BookId);
        var bookIds = await db.Books.Where(book => (book.Isbn != null && sampleIsbns.Contains(book.Isbn)) ||
                (book.Description != null && book.Description.Contains(StagingDemoMarkers.BookDescription)) ||
                markedCopyBookIds.Contains(book.Id))
            .Select(book => book.Id).ToArrayAsync(cancellationToken);
        var sampleCopyIds = await db.BookCopies.Where(copy =>
                copy.CopyCode.StartsWith("DEMO-CPY-") || copy.CopyCode.StartsWith(CopyCodePrefix) || bookIds.Contains(copy.BookId))
            .Select(copy => copy.Id).ToArrayAsync(cancellationToken);
        var sampleReaderEmails = Enumerable.Range(1, ReaderTarget)
            .Select(index => $"stg-demo-reader-{index:000}@example.test").ToArray();
        var readerIds = await db.ReaderAccounts.Where(reader => reader.StudentOrStaffCode.StartsWith(ReaderCodePrefix) ||
                sampleReaderEmails.Contains(reader.Email) ||
                (reader.Address != null && reader.Address.Contains(StagingDemoMarkers.ReaderAddress)))
            .Select(reader => reader.Id).ToArrayAsync(cancellationToken);
        var loanIds = await db.BookLoans.Where(loan => bookIds.Contains(loan.BookId) ||
                (loan.BookCopyId.HasValue && sampleCopyIds.Contains(loan.BookCopyId.Value)) ||
                readerIds.Contains(loan.ReaderAccountId))
            .Select(loan => loan.Id).ToArrayAsync(cancellationToken);
        var holdIds = await db.BookHolds.Where(hold => bookIds.Contains(hold.BookId) ||
                sampleCopyIds.Contains(hold.BookCopyId ?? 0) || sampleCopyIds.Contains(hold.ExpiredBookCopyId ?? 0) ||
                readerIds.Contains(hold.ReaderAccountId))
            .Select(hold => hold.Id).ToArrayAsync(cancellationToken);
        var readerHasLoanOutsideSampleBooks = await db.BookLoans.AnyAsync(loan => readerIds.Contains(loan.ReaderAccountId) &&
            !bookIds.Contains(loan.BookId) && (!loan.BookCopyId.HasValue || !sampleCopyIds.Contains(loan.BookCopyId.Value)), cancellationToken);
        var externalLoanUsesSampleHold = await db.BookLoans.AnyAsync(loan => loan.SourceBookHoldId.HasValue &&
            holdIds.Contains(loan.SourceBookHoldId.Value) && !loanIds.Contains(loan.Id), cancellationToken);
        if (readerHasLoanOutsideSampleBooks || externalLoanUsesSampleHold)
            throw new InvalidOperationException(
                "Không thể reset an toàn: một phiếu ngoài bộ dữ liệu đang liên kết với bạn đọc/đơn giữ mẫu.");
        var incidentIds = await db.BookIncidents.Where(incident => loanIds.Contains(incident.BookLoanId) ||
                sampleCopyIds.Contains(incident.BookCopyId))
            .Select(incident => incident.Id).ToArrayAsync(cancellationToken);
        var hasNonSeedIncidentHistory = await db.BookIncidentAuditLogs.AnyAsync(log =>
            (loanIds.Contains(log.BookLoanId) ||
             (log.BookIncidentId.HasValue && incidentIds.Contains(log.BookIncidentId.Value)) ||
             (log.BookCopyId.HasValue && sampleCopyIds.Contains(log.BookCopyId.Value))) &&
            log.Actor != "STAGING-DEMO-SEEDER", cancellationToken);
        var hasAdjustmentVoucher = await db.IncidentAdjustmentVouchers.AnyAsync(voucher =>
            loanIds.Contains(voucher.BookLoanId) || incidentIds.Contains(voucher.BookIncidentId) ||
            sampleCopyIds.Contains(voucher.BookCopyId), cancellationToken);
        var hasCompensationAdjustmentHistory = await db.CompensationAdjustmentHistories.AnyAsync(history =>
            db.BookIncidentCompensations.Any(compensation => incidentIds.Contains(compensation.BookIncidentId) &&
                compensation.Id == history.BookIncidentCompensationId), cancellationToken);
        if (hasNonSeedIncidentHistory || hasAdjustmentVoucher || hasCompensationAdjustmentHistory)
            throw new InvalidOperationException(
                "Không thể reset an toàn: dữ liệu mẫu có lịch sử sự cố/bút toán điều chỉnh bất biến do người dùng tạo; lịch sử được giữ nguyên.");
        var feeIds = await db.ReaderFees.Where(fee => readerIds.Contains(fee.ReaderAccountId) ||
                (fee.BookLoanId.HasValue && loanIds.Contains(fee.BookLoanId.Value)))
            .Select(fee => fee.Id).ToArrayAsync(cancellationToken);
        var paymentIds = await db.FeePayments.Where(payment => readerIds.Contains(payment.ReaderAccountId) ||
                db.FeePaymentAllocations.Any(allocation => allocation.FeePaymentId == payment.Id && feeIds.Contains(allocation.ReaderFeeId)))
            .Select(payment => payment.Id).ToArrayAsync(cancellationToken);
        var mixedPayment = await db.FeePaymentAllocations.AnyAsync(allocation => paymentIds.Contains(allocation.FeePaymentId) &&
            !feeIds.Contains(allocation.ReaderFeeId), cancellationToken);
        if (mixedPayment)
            throw new InvalidOperationException("Không thể reset an toàn: khoản thanh toán mẫu gộp khoản thu ngoài phạm vi.");
        var allocationIds = await db.FeePaymentAllocations.Where(allocation => paymentIds.Contains(allocation.FeePaymentId) ||
                feeIds.Contains(allocation.ReaderFeeId))
            .Select(allocation => allocation.Id).ToArrayAsync(cancellationToken);
        var operationIds = await db.DirectLoanOperationItems.Where(item => loanIds.Contains(item.BookLoanId))
            .Select(item => item.RequestId).Distinct().ToArrayAsync(cancellationToken);
        var mixedOperation = await db.DirectLoanOperationItems.AnyAsync(item => operationIds.Contains(item.RequestId) &&
            !loanIds.Contains(item.BookLoanId), cancellationToken);
        if (mixedOperation)
            throw new InvalidOperationException("Không thể reset an toàn: một thao tác mượn gộp bản sao staging và bản sao ngoài phạm vi.");

        var demoWarehouses = await db.Warehouses.Where(warehouse => warehouse.Code.StartsWith("DEMO-KHO-"))
            .Select(warehouse => warehouse.Id).ToArrayAsync(cancellationToken);
        var demoShelves = await db.Shelves.Where(shelf => demoWarehouses.Contains(shelf.WarehouseId))
            .Select(shelf => shelf.Id).ToArrayAsync(cancellationToken);
        var externalCopyOnDemoShelf = await db.BookCopies.AnyAsync(copy => demoShelves.Contains(copy.ShelfId) &&
            !sampleCopyIds.Contains(copy.Id), cancellationToken);
        if (externalCopyOnDemoShelf)
            throw new InvalidOperationException("Không thể reset an toàn: có bản sao ngoài phạm vi trên kệ demo.");

        await db.FeeReceiptLines.Where(line => paymentIds.Contains(line.FeePaymentId)).ExecuteDeleteAsync(cancellationToken);
        await db.FeeReceipts.Where(receipt => paymentIds.Contains(receipt.FeePaymentId)).ExecuteDeleteAsync(cancellationToken);
        await db.FeePaymentAllocations.Where(allocation => allocationIds.Contains(allocation.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.FeePayments.Where(payment => paymentIds.Contains(payment.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.FeeWaiverRequests.Where(request => feeIds.Contains(request.ReaderFeeId)).ExecuteDeleteAsync(cancellationToken);
        await db.IncidentAdjustmentVouchers.Where(voucher => loanIds.Contains(voucher.BookLoanId) ||
            incidentIds.Contains(voucher.BookIncidentId) || sampleCopyIds.Contains(voucher.BookCopyId)).ExecuteDeleteAsync(cancellationToken);
        // Only this fixture actor's immutable event rows are reset with their disposable sample entities.
        // Operator-created audit rows are detected above and make the reset abort before any deletion.
        await db.BookIncidentAuditLogs.Where(log => log.Actor == "STAGING-DEMO-SEEDER" &&
            (loanIds.Contains(log.BookLoanId) ||
             (log.BookIncidentId.HasValue && incidentIds.Contains(log.BookIncidentId.Value)) ||
             (log.BookCopyId.HasValue && sampleCopyIds.Contains(log.BookCopyId.Value))))
            .ExecuteDeleteAsync(cancellationToken);
        await db.BookIncidentCompensations.Where(compensation => incidentIds.Contains(compensation.BookIncidentId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.BookIncidents.Where(incident => incidentIds.Contains(incident.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.DirectLoanOperationItems.Where(item => operationIds.Contains(item.RequestId)).ExecuteDeleteAsync(cancellationToken);
        await db.DirectLoanOperations.Where(operation => operationIds.Contains(operation.RequestId)).ExecuteDeleteAsync(cancellationToken);
        await db.ReaderFees.Where(fee => feeIds.Contains(fee.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.BookLoans.Where(loan => loanIds.Contains(loan.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.BookHolds.Where(hold => holdIds.Contains(hold.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.LibraryCards.Where(card => readerIds.Contains(card.ReaderAccountId)).ExecuteDeleteAsync(cancellationToken);
        await db.BookCopyStatusHistories.Where(history => sampleCopyIds.Contains(history.BookCopyId)).ExecuteDeleteAsync(cancellationToken);
        await db.BookCopies.Where(copy => sampleCopyIds.Contains(copy.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.ReaderAccounts.Where(reader => readerIds.Contains(reader.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.Books.Where(book => bookIds.Contains(book.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.Shelves.Where(shelf => demoShelves.Contains(shelf.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.Warehouses.Where(warehouse => demoWarehouses.Contains(warehouse.Id)).ExecuteDeleteAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private async Task EnsureSupplementalBooksAndCopiesAsync(CancellationToken cancellationToken)
    {
        var authorIds = await db.Authors.AsNoTracking().Where(author => Authors.Contains(author.Name))
            .ToDictionaryAsync(author => author.Name, author => author.Id, cancellationToken);
        var categoryIds = await db.Categories.AsNoTracking().Where(category => Categories.Contains(category.Name))
            .ToDictionaryAsync(category => category.Name, category => category.Id, cancellationToken);
        var shelves = await db.Shelves.AsNoTracking()
            .Where(shelf => shelf.Code.StartsWith("DEMO-K") && shelf.Warehouse != null &&
                shelf.Warehouse.Code.StartsWith("DEMO-KHO-"))
            .OrderBy(shelf => shelf.Code).ToListAsync(cancellationToken);
        if (authorIds.Count != Authors.Length || categoryIds.Count != Categories.Length || shelves.Count == 0)
            throw new InvalidOperationException("Không tìm thấy đủ tác giả, thể loại hoặc kệ demo để tạo dữ liệu staging.");

        var additionalBooks = new List<Book>();
        for (var sequence = 26; sequence <= BookTarget; sequence++)
        {
            var isbn = CreateSampleIsbn(sequence);
            if (await db.Books.AnyAsync(book => book.Isbn == isbn, cancellationToken)) continue;

            var category = Categories[(sequence - 1) % Categories.Length];
            var categoryLabel = category.Replace(" và ", " - ", StringComparison.Ordinal);
            var book = new Book
            {
                Title = $"{Titles[(sequence - 1) % Titles.Length]} {categoryLabel} - Tập {sequence:000}",
                Isbn = isbn,
                AuthorId = authorIds[Authors[(sequence - 1) % Authors.Length]],
                CategoryId = categoryIds[category],
                Publisher = Publishers[(sequence - 1) % Publishers.Length],
                PublicationYear = 1990 + sequence % 36,
                PageCount = 120 + sequence % 401,
                Description = $"Đầu sách hư cấu được tạo cho môi trường nghiệm thu staging. {StagingDemoMarkers.BookDescription}"
            };
            book.BookAuthors.Add(new BookAuthor { AuthorId = book.AuthorId, SortOrder = 0 });
            additionalBooks.Add(book);
        }

        db.Books.AddRange(additionalBooks);
        await db.SaveChangesAsync(cancellationToken);

        var sampleIsbns = Enumerable.Range(1, BookTarget).Select(CreateSampleIsbn).ToArray();
        var demoBooks = await db.Books.AsNoTracking().Where(book => book.Isbn != null && sampleIsbns.Contains(book.Isbn))
            .OrderBy(book => book.Isbn).ToListAsync(cancellationToken);
        if (demoBooks.Count != BookTarget)
            throw new InvalidOperationException($"Cần có {BookTarget} đầu sách demo, hiện tìm thấy {demoBooks.Count}.");

        var newCopies = new List<BookCopy>();
        for (var bookIndex = 25; bookIndex < demoBooks.Count; bookIndex++)
        {
            var book = demoBooks[bookIndex];
            var sequence = bookIndex + 1;
            var quantity = sequence <= 125 ? 3 : 2; // 100 extra copies: total 500 across 200 titles.
            for (var copyIndex = 1; copyIndex <= quantity; copyIndex++)
            {
                var code = $"{CopyCodePrefix}{sequence:000}-{copyIndex:00}";
                if (await db.BookCopies.AnyAsync(copy => copy.CopyCode == code, cancellationToken)) continue;
                newCopies.Add(new BookCopy
                {
                    BookId = book.Id,
                    ShelfId = shelves[(sequence + copyIndex) % shelves.Count].Id,
                    CopyCode = code,
                    ReceivedDate = new DateOnly(2026, 1, 15).AddDays(sequence % 90),
                    CoverPrice = 85000m + sequence * 500m,
                    Status = BookCopyStatus.Available,
                    PhysicalCondition = BookCopyCondition.Good,
                    Note = "Bản sao hư cấu của bộ dữ liệu nghiệm thu staging."
                });
            }
        }

        db.BookCopies.AddRange(newCopies);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureReadersAndLoansAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var hasher = new PasswordHasher<ReaderAccount>();
        var readersByCode = await db.ReaderAccounts
            .Where(reader => reader.StudentOrStaffCode.StartsWith(ReaderCodePrefix))
            .ToDictionaryAsync(reader => reader.StudentOrStaffCode, cancellationToken);

        for (var index = 1; index <= ReaderTarget; index++)
        {
            var code = $"{ReaderCodePrefix}{index:000}";
            if (readersByCode.ContainsKey(code)) continue;

            var reader = new ReaderAccount
            {
                FullName = $"{LastNames[(index - 1) % LastNames.Length]} {FirstNames[((index - 1) / LastNames.Length) % FirstNames.Length]} {index:00}",
                DateOfBirth = new DateOnly(1988 + index % 15, 1 + index % 12, 1 + index % 27),
                Email = $"stg-demo-reader-{index:000}@example.test",
                PhoneNumber = $"090000{index:0000}",
                Address = $"Địa chỉ mẫu {index:000}, chỉ dùng trong staging. {StagingDemoMarkers.ReaderAddress}",
                StudentOrStaffCode = code,
                Status = "Đang hoạt động",
                EmailConfirmed = true,
                CreatedAtUtc = nowUtc.AddDays(-index * 13),
                UpdatedAtUtc = nowUtc.AddDays(-index * 13)
            };
            var privateRandomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            reader.PasswordHash = hasher.HashPassword(reader, privateRandomPassword);
            db.ReaderAccounts.Add(reader);
            readersByCode.Add(code, reader);
        }
        await db.SaveChangesAsync(cancellationToken);

        var readerCodes = Enumerable.Range(1, ReaderTarget).Select(index => $"{ReaderCodePrefix}{index:000}").ToArray();
        var readers = await db.ReaderAccounts.Where(reader => readerCodes.Contains(reader.StudentOrStaffCode))
            .OrderBy(reader => reader.StudentOrStaffCode).ToListAsync(cancellationToken);
        var demoCopies = await db.BookCopies
            .Where(copy => copy.CopyCode.StartsWith("DEMO-CPY-") || copy.CopyCode.StartsWith(CopyCodePrefix))
            .Include(copy => copy.Book)
            .OrderBy(copy => copy.CopyCode).ToListAsync(cancellationToken);
        if (readers.Count != ReaderTarget || demoCopies.Count < LoanTarget)
            throw new InvalidOperationException("Không đủ bạn đọc hoặc bản sao demo để tạo phiếu mượn staging.");

        var copyIds = demoCopies.Select(copy => copy.Id).ToArray();
        var existingLoans = await db.BookLoans.Where(loan => loan.BookCopyId.HasValue && copyIds.Contains(loan.BookCopyId.Value))
            .ToListAsync(cancellationToken);
        var usedCopyIds = existingLoans.Where(loan => loan.BookCopyId.HasValue)
            .Select(loan => loan.BookCopyId!.Value).ToHashSet();
        var existingPairs = existingLoans.Select(loan => (loan.ReaderAccountId, loan.BookCopyId)).ToHashSet();
        var dailyRate = await db.LoanPolicies.AsNoTracking().Where(policy => policy.Id == LoanPolicy.SingletonId)
            .Select(policy => policy.DailyOverdueFee).SingleOrDefaultAsync(cancellationToken);
        var workingSchedule = new WorkingScheduleService(db);
        // The shared schedule service's established first-use defaults mark missing weekdays open.
        await workingSchedule.GetOpenDatesAsync(LibraryCalendarDate.Today(), LibraryCalendarDate.Today(), cancellationToken);
        var compensationPolicy = await db.CompensationPolicies.AsNoTracking()
            .SingleOrDefaultAsync(policy => policy.Id == CompensationPolicy.SingletonId, cancellationToken);
        var today = LibraryCalendarDate.Today();
        var loanDays = await db.LoanPolicies.AsNoTracking().Where(policy => policy.Id == LoanPolicy.SingletonId)
            .Select(policy => (int?)policy.LoanDays).SingleOrDefaultAsync(cancellationToken) ?? BookLoanService.DefaultLoanDays;

        for (var index = 0; index < LoanTarget; index++)
        {
            var reader = readers[index % ReaderTarget];
            var copy = demoCopies[index];
            if (existingPairs.Contains((reader.Id, (long?)copy.Id))) continue;
            if (usedCopyIds.Contains(copy.Id))
            {
                copy = demoCopies.FirstOrDefault(candidate => !usedCopyIds.Contains(candidate.Id))
                    ?? throw new InvalidOperationException("Không còn bản sao demo khả dụng cho phiếu mượn.");
            }

            var loanDate = index switch
            {
                < 20 => today.AddDays(-Math.Max(1, loanDays / 2)),
                < 40 => today.AddDays(-loanDays - 10),
                < 60 => today.AddDays(-loanDays - 30),
                < 80 => today.AddDays(-loanDays - 40),
                _ => today.AddDays(-loanDays)
            };
            var originalDueDate = loanDate.AddDays(loanDays);
            var dueDate = await workingSchedule.AdjustLoanDueDateAsync(originalDueDate, cancellationToken);
            DateOnly? returnDate = index switch
            {
                >= 40 and < 60 => dueDate,
                _ => null
            };
            if (index is >= 60 and < 80)
                returnDate = await workingSchedule.AdjustLoanDueDateAsync(dueDate.AddDays(3), cancellationToken);
            var isIncident = index >= 80;
            if (isIncident) returnDate = today;

            var loan = new BookLoan
            {
                BookId = copy.BookId,
                BookCopyId = copy.Id,
                ReaderAccountId = reader.Id,
                LoanDate = loanDate,
                OriginalDueDate = originalDueDate,
                DueDate = dueDate,
                RenewalCount = 0,
                CreatedAtUtc = HoldPickupExpirySchedule.ToUtc(loanDate, new TimeOnly(9, 0)),
                ReturnedAtUtc = returnDate is { } returnedDate
                    ? HoldPickupExpirySchedule.ToUtc(returnedDate, new TimeOnly(16, 0))
                    : null,
                CloseReason = isIncident
                    ? (index < 90 ? BookLoanCloseReason.Lost : BookLoanCloseReason.Damaged)
                    : returnDate.HasValue ? BookLoanCloseReason.Returned : null
            };

            if (index is >= 60 and < 80 && returnDate.HasValue && dailyRate is >= 0)
            {
                var openDates = await workingSchedule.GetConfiguredOpenDatesAsync(
                    dueDate.AddDays(1), returnDate.Value, cancellationToken);
                var fee = OverdueFeeCalculator.Calculate(dueDate, returnDate.Value, dailyRate.Value, openDates);
                loan.OverdueDaysAtReturn = fee.OverdueDays;
                loan.DailyOverdueFeeAtReturn = fee.DailyRate;
                loan.OverdueFee = fee.EstimatedFee;
                if (fee.EstimatedFee > 0m)
                    loan.OverdueReceivable = new ReaderFee
                    {
                        ReaderAccountId = reader.Id,
                        Amount = fee.EstimatedFee,
                        PaidAmount = 0m,
                        IsPaid = false,
                        Status = "Chưa thanh toán",
                        Description = "Phí trễ hạn của phiếu demo staging.",
                        CreatedAtUtc = loan.ReturnedAtUtc!.Value
                    };
            }

            db.BookLoans.Add(loan);
            usedCopyIds.Add(copy.Id);
            existingPairs.Add((reader.Id, (long?)copy.Id));
            var statusBeforeLoan = copy.Status;
            copy.Status = BookCopyStatus.OnLoan;
            db.BookCopyStatusHistories.Add(new BookCopyStatusHistory
            {
                BookCopy = copy,
                FromStatus = statusBeforeLoan,
                ToStatus = BookCopyStatus.OnLoan,
                Reason = "Cấp bản sao cho phiếu mượn demo staging.",
                ChangedBy = "STAGING-DEMO-SEEDER",
                ChangedAtUtc = loan.CreatedAtUtc
            });

            if (!isIncident)
            {
                if (returnDate.HasValue)
                {
                    copy.Status = BookCopyStatus.Available;
                    db.BookCopyStatusHistories.Add(new BookCopyStatusHistory
                    {
                        BookCopy = copy,
                        FromStatus = BookCopyStatus.OnLoan,
                        ToStatus = BookCopyStatus.Available,
                        Reason = "Trả bản sao theo phiếu mượn demo staging.",
                        ChangedBy = "STAGING-DEMO-SEEDER",
                        ChangedAtUtc = loan.ReturnedAtUtc!.Value
                    });
                }
                continue;
            }

            var incidentType = index < 90 ? BookIncidentType.Lost : BookIncidentType.Damaged;
            var reportedAtUtc = loan.ReturnedAtUtc!.Value;
            const string seedActor = "STAGING-DEMO-SEEDER";
            var reason = $"Sự cố {incidentType} mẫu phục vụ nghiệm thu staging.";
            var previousCopyStatus = copy.Status;
            copy.Status = BookCopyStatus.Removed;
            copy.StatusReason = reason;
            if (incidentType == BookIncidentType.Damaged)
                copy.PhysicalCondition = BookCopyCondition.MajorDamage;

            db.BookCopyStatusHistories.Add(new BookCopyStatusHistory
            {
                BookCopy = copy,
                FromStatus = previousCopyStatus,
                ToStatus = BookCopyStatus.Removed,
                Reason = reason,
                ChangedBy = seedActor,
                ChangedAtUtc = reportedAtUtc
            });
            var incident = new BookIncident
            {
                BookLoan = loan,
                BookCopy = copy,
                IncidentType = incidentType,
                Description = reason,
                ReportedBy = seedActor,
                CreatedAtUtc = reportedAtUtc
            };
            db.BookIncidents.Add(incident);
            var initialCompensation = compensationPolicy is not null && copy.CoverPrice is > 0m
                ? decimal.Round(copy.CoverPrice.Value * compensationPolicy.GetRateFor(incidentType), 2)
                : (decimal?)null;
            if (initialCompensation.HasValue)
            {
                db.BookIncidentCompensations.Add(new BookIncidentCompensation
                {
                    BookIncident = incident,
                    RecordedCoverPrice = copy.CoverPrice,
                    AppliedRate = compensationPolicy!.GetRateFor(incidentType),
                    InitialAmount = initialCompensation.Value,
                    CurrentAmount = initialCompensation.Value,
                    CreatedAtUtc = reportedAtUtc
                });
            }

            db.BookIncidentAuditLogs.Add(new BookIncidentAuditLog
            {
                BookLoan = loan, BookIncident = incident, BookCopy = copy,
                OperationType = IncidentAuditOperations.RecordIncident, Actor = seedActor,
                OccurredAtUtc = reportedAtUtc, IncidentType = incidentType, Description = reason,
                CopyStatusResult = BookCopyStatus.Removed,
                Summary = $"Ghi nhận sự cố mẫu {incidentType} cho bản sao {copy.CopyCode}."
            });
            db.BookIncidentAuditLogs.Add(new BookIncidentAuditLog
            {
                BookLoan = loan, BookIncident = incident, BookCopy = copy,
                OperationType = IncidentAuditOperations.CloseLoan, Actor = seedActor,
                OccurredAtUtc = reportedAtUtc, CloseReason = incidentType, IncidentDate = today,
                OverdueFee = 0m, InitialCompensationAmount = initialCompensation,
                Summary = $"Đóng phiếu demo do {incidentType}; không phát sinh phí trễ hạn trong bản ghi mẫu này."
            });
            db.AuditLogs.Add(AuditLogService.Create(seedActor, "RecordBookIncident",
                $"Sự cố staging {incidentType} cho phiếu demo, bản sao {copy.CopyCode}.", "127.0.0.1"));
        }
    }

    private static string GetState(BookLoan loan, DateOnly today)
    {
        if (loan.CloseReason == BookLoanCloseReason.Lost) return "Đóng do mất";
        if (loan.CloseReason == BookLoanCloseReason.Damaged) return "Đóng do hỏng nặng";
        if (!loan.ReturnedAtUtc.HasValue)
            return loan.DueDate < today ? "Quá hạn chưa trả" : "Đang mượn";
        var returnedDate = LibraryCalendarDate.DateFromUtc(loan.ReturnedAtUtc.Value);
        return returnedDate > loan.DueDate ? "Đã trả trễ hạn" : "Đã trả đúng hạn";
    }

    private static string CreateSampleIsbn(int sequence)
    {
        var body = $"979000000{sequence:000}";
        var sum = body.Select((digit, index) => (digit - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
        var checkDigit = (10 - sum % 10) % 10;
        return $"{body}{checkDigit}";
    }
}
