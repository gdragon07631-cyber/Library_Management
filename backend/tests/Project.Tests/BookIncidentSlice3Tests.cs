using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;
using Xunit;

namespace Project.Tests;

public class BookIncidentSlice3Tests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly BookIncidentService _incidentService;
    private readonly CompensationService _compensationService;
    private readonly BookLoanService _bookLoanService;

    public BookIncidentSlice3Tests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDbContext(options);
        _incidentService = new BookIncidentService(_db);
        _compensationService = new CompensationService(_db);
        _bookLoanService = new BookLoanService(_db, new WorkingScheduleService(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private async Task SeedCompensationPolicyAsync(decimal lostRate = 1.0m, decimal damagedRate = 0.5m)
    {
        var policy = new CompensationPolicy
        {
            Id = CompensationPolicy.SingletonId,
            LostRate = lostRate,
            DamagedRate = damagedRate,
            DefaultRate = 1.0m,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedBy = "admin"
        };
        _db.CompensationPolicies.Add(policy);
        await _db.SaveChangesAsync();
    }

    private async Task<(Book Book, BookCopy Copy, ReaderAccount Reader, BookLoan Loan)> SeedLoanAsync(
        DateOnly? loanDate = null,
        DateOnly? dueDate = null,
        decimal? coverPrice = 100_000m,
        decimal initialOverdueFee = 0m,
        decimal initialReaderBalance = 0m,
        string copyStatus = BookCopyStatus.OnLoan,
        string physicalCondition = BookCopyCondition.Good)
    {
        if (!await _db.LoanPolicies.AnyAsync())
            _db.LoanPolicies.Add(new LoanPolicy { Id = LoanPolicy.SingletonId, DailyOverdueFee = 2_000m });
        if (!await _db.WeeklyWorkingSchedules.AnyAsync())
            _db.WeeklyWorkingSchedules.AddRange(Enum.GetValues<DayOfWeek>().Select(day => new WeeklyWorkingSchedule { DayOfWeek = day, IsOpen = true }));
        var author = new Author { Name = "Tác giả " + Guid.NewGuid().ToString("N")[..6] };
        var category = new Category { Name = "Thể loại " + Guid.NewGuid().ToString("N")[..6] };
        _db.Authors.Add(author);
        _db.Categories.Add(category);

        var book = new Book
        {
            Title = "Giáo trình Cấu trúc dữ liệu",
            Author = author,
            Category = category
        };
        _db.Books.Add(book);

        var warehouse = new Warehouse { Code = "KHO-" + Guid.NewGuid().ToString("N")[..4], Name = "Kho Tổng" };
        _db.Warehouses.Add(warehouse);

        var shelf = new Shelf { Code = "KE-" + Guid.NewGuid().ToString("N")[..4], Name = "Kệ A1", Warehouse = warehouse };
        _db.Shelves.Add(shelf);

        var copy = new BookCopy
        {
            Book = book,
            Shelf = shelf,
            CopyCode = "CP-" + Guid.NewGuid().ToString("N")[..6],
            Status = copyStatus,
            PhysicalCondition = physicalCondition,
            CoverPrice = coverPrice
        };
        _db.BookCopies.Add(copy);

        var cardType = new LibraryCardType { Name = "Thẻ chuẩn", MaxBooks = 5 };
        _db.LibraryCardTypes.Add(cardType);

        var reader = new ReaderAccount
        {
            FullName = "Trần Thị Bạn Đọc",
            Email = $"reader_{Guid.NewGuid():N}@example.com",
            StudentOrStaffCode = "SV" + Guid.NewGuid().ToString("N")[..6],
            PhoneNumber = "0912345678",
            PasswordHash = "hash",
            Status = "Đang hoạt động",
            OutstandingBalance = initialReaderBalance
        };
        _db.ReaderAccounts.Add(reader);

        var card = new LibraryCard
        {
            CardCode = "CARD-" + Guid.NewGuid().ToString("N")[..6],
            ReaderAccount = reader,
            LibraryCardType = cardType,
            IssuedOn = DateOnly.FromDateTime(DateTime.Today.AddDays(-30)),
            ExpiresOn = DateOnly.FromDateTime(DateTime.Today.AddYears(1)),
            Status = "Đang hoạt động"
        };
        _db.LibraryCards.Add(card);

        var effectiveLoanDate = loanDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-14));
        var effectiveDueDate = dueDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(7));

        var loan = new BookLoan
        {
            Book = book,
            BookId = book.Id,
            BookCopy = copy,
            BookCopyId = copy.Id,
            ReaderAccount = reader,
            ReaderAccountId = reader.Id,
            LoanDate = effectiveLoanDate,
            OriginalDueDate = effectiveDueDate,
            DueDate = effectiveDueDate,
            OverdueFee = initialOverdueFee,
            ReturnedAtUtc = null,
            CloseReason = null
        };
        _db.BookLoans.Add(loan);

        await _db.SaveChangesAsync();
        return (book, copy, reader, loan);
    }

    // -------------------------------------------------------------------------
    // TEST 01: Đóng phiếu mượn khi ghi nhận sự cố Mất
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test01_RecordLostCopy_ClosesLoan_WithReasonLost_AndSetsReturnedAtUtc()
    {
        var (_, copy, _, loan) = await SeedLoanAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Bạn đọc làm mất sách trong ký túc xá",
            StaffEmail = "librarian@library.edu.vn"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.NotNull(outcome.ClosedLoan);
        Assert.Equal(loan.Id, outcome.ClosedLoan.Id);
        Assert.NotNull(outcome.ClosedLoan.ReturnedAtUtc);
        Assert.True(outcome.ClosedLoan.IsReturned);
        Assert.Equal(BookLoanCloseReason.Lost, outcome.ClosedLoan.CloseReason);
        Assert.Equal(BookLoanCloseReason.Lost, outcome.CloseReason);

        // Kiểm tra trong CSDL
        var loanInDb = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(loanInDb);
        Assert.NotNull(loanInDb.ReturnedAtUtc);
        Assert.True(loanInDb.IsReturned);
        Assert.Equal(BookLoanCloseReason.Lost, loanInDb.CloseReason);
    }

    // -------------------------------------------------------------------------
    // TEST 02: Đóng phiếu mượn khi ghi nhận sự cố Hỏng nặng
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test02_RecordDamagedCopy_ClosesLoan_WithReasonDamaged_AndSetsReturnedAtUtc()
    {
        var (_, copy, _, loan) = await SeedLoanAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Sách bị ngấm nước mục rách hoàn toàn các trang ruột",
            StaffEmail = "librarian@library.edu.vn"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.NotNull(outcome.ClosedLoan);
        Assert.Equal(loan.Id, outcome.ClosedLoan.Id);
        Assert.NotNull(outcome.ClosedLoan.ReturnedAtUtc);
        Assert.True(outcome.ClosedLoan.IsReturned);
        Assert.Equal(BookLoanCloseReason.Damaged, outcome.ClosedLoan.CloseReason);
        Assert.Equal(BookLoanCloseReason.Damaged, outcome.CloseReason);

        // Kiểm tra trong CSDL
        var loanInDb = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(loanInDb);
        Assert.NotNull(loanInDb.ReturnedAtUtc);
        Assert.True(loanInDb.IsReturned);
        Assert.Equal(BookLoanCloseReason.Damaged, loanInDb.CloseReason);
    }

    // -------------------------------------------------------------------------
    // TEST 03: Phân biệt rõ lý do đóng Mất/Hỏng với lý do "Đã trả" thông thường
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test03_DistinguishesBetween_LostOrDamaged_AndNormalReturn()
    {
        // Phiếu 1: Sự cố Mất
        var (_, copy1, _, loan1) = await SeedLoanAsync();
        await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan1.Id,
            BookCopyId = copy1.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách"
        });

        // Phiếu 2: Sự cố Hỏng nặng
        var (_, copy2, _, loan2) = await SeedLoanAsync();
        await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan2.Id,
            BookCopyId = copy2.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Hỏng nặng sách"
        });

        // Phiếu 3: Trả sách thông thường
        var (_, copy3, _, loan3) = await SeedLoanAsync();
        var returnOutcome = await _bookLoanService.ReturnAsync(loan3.Id, DateTime.UtcNow);
        Assert.True(returnOutcome.IsSuccess);

        var refreshed1 = await _db.BookLoans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == loan1.Id);
        var refreshed2 = await _db.BookLoans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == loan2.Id);
        var refreshed3 = await _db.BookLoans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == loan3.Id);

        Assert.Equal(BookLoanCloseReason.Lost, refreshed1!.CloseReason);
        Assert.Equal(BookLoanCloseReason.Damaged, refreshed2!.CloseReason);
        Assert.Equal(BookLoanCloseReason.Returned, refreshed3!.CloseReason);

        Assert.NotEqual(refreshed1.CloseReason, refreshed3.CloseReason);
        Assert.NotEqual(refreshed2.CloseReason, refreshed3.CloseReason);
    }

    // -------------------------------------------------------------------------
    // TEST 04: Phiếu mượn đã đóng không còn xuất hiện trong danh sách phiếu đang mở
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test04_ClosedLoan_DoesNotAppearInOpenLoansList()
    {
        var (_, copy, _, loan) = await SeedLoanAsync();

        // Trước khi ghi nhận: xuất hiện trong danh sách
        var openLoansBefore = await _incidentService.GetOpenLoansAsync();
        Assert.Contains(openLoansBefore, l => l.LoanId == loan.Id);

        // Ghi nhận sự cố
        await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Báo mất"
        });

        // Sau khi ghi nhận: phiếu đã đóng, không còn xuất hiện
        var openLoansAfter = await _incidentService.GetOpenLoansAsync();
        Assert.DoesNotContain(openLoansAfter, l => l.LoanId == loan.Id);
    }

    // -------------------------------------------------------------------------
    // TEST 05: Ngăn ghi nhận sự cố trên phiếu mượn đã đóng do Mất
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test05_CannotRecordIncident_OnAlreadyClosedLoan_DueToLost()
    {
        var (_, copy, _, loan) = await SeedLoanAsync();

        // Lần 1: Ghi nhận thành công
        var outcome1 = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Báo mất lần đầu"
        });
        Assert.True(outcome1.IsSuccess);

        // Lần 2: Thử ghi nhận lại trên cùng phiếu mượn đã đóng
        var outcome2 = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Báo hỏng sau khi đã đóng"
        });

        Assert.False(outcome2.IsSuccess);
        Assert.True(outcome2.ErrorMessage!.Contains("đã đóng hoặc đã được trả") || outcome2.ErrorMessage.Contains("đã bị loại khỏi kho"));
    }

    // -------------------------------------------------------------------------
    // TEST 06: Ngăn ghi nhận sự cố trên phiếu mượn đã đóng do Hỏng nặng
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test06_CannotRecordIncident_OnAlreadyClosedLoan_DueToDamaged()
    {
        var (_, copy, _, loan) = await SeedLoanAsync();

        // Lần 1: Ghi nhận Hỏng nặng
        var outcome1 = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Hỏng nặng lần đầu"
        });
        Assert.True(outcome1.IsSuccess);

        // Lần 2: Thử ghi nhận lại trên cùng phiếu mượn đã đóng
        var outcome2 = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Báo mất sau khi đã đóng hỏng nặng"
        });

        Assert.False(outcome2.IsSuccess);
        Assert.True(outcome2.ErrorMessage!.Contains("đã đóng hoặc đã được trả") || outcome2.ErrorMessage.Contains("đã bị loại khỏi kho"));
    }

    // -------------------------------------------------------------------------
    // TEST 07: Ngăn ghi nhận sự cố trên phiếu mượn đã trả thông thường
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test07_CannotRecordIncident_OnAlreadyReturnedLoan()
    {
        var (_, copy, _, loan) = await SeedLoanAsync();

        // Trả sách thông thường
        var returnOutcome = await _bookLoanService.ReturnAsync(loan.Id, DateTime.UtcNow);
        Assert.True(returnOutcome.IsSuccess);

        // Thử ghi nhận sự cố trên phiếu đã trả
        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Thử ghi nhận sự cố trên phiếu đã trả"
        });

        Assert.False(outcome.IsSuccess);
        Assert.Contains("đã đóng hoặc đã được trả", outcome.ErrorMessage);
    }

    // -------------------------------------------------------------------------
    // TEST 08: Ngăn trả sách thông thường (ReturnAsync) trên phiếu đã đóng do sự cố
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test08_CannotReturnBook_WhenLoanAlreadyClosedDueToIncident()
    {
        var (_, copy, _, loan) = await SeedLoanAsync();

        // Ghi nhận sự cố Mất -> đóng phiếu
        var incidentOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách"
        });
        Assert.True(incidentOutcome.IsSuccess);

        // Thao tác trả sách thông thường phải bị từ chối
        var returnOutcome = await _bookLoanService.ReturnAsync(loan.Id, DateTime.UtcNow);
        Assert.False(returnOutcome.IsSuccess);
        Assert.True(returnOutcome.IsConflict);
    }

    // -------------------------------------------------------------------------
    // TEST 09: Tính đúng phí trễ hạn khi phiếu mượn quá hạn (2.000 VNĐ / ngày trễ)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test09_CalculateOverdueFee_WhenLoanOverdue_DailyRate2000Vnd()
    {
        // Phiếu mượn có hạn trả là 5 ngày trước ngày ghi nhận sự cố
        var incidentTime = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var incidentDate = DateOnly.FromDateTime(incidentTime);
        var dueDate = incidentDate.AddDays(-5); // Trễ đúng 5 ngày
        var loanDate = dueDate.AddDays(-14);

        var (_, copy, _, loan) = await SeedLoanAsync(loanDate: loanDate, dueDate: dueDate);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách sau khi quá hạn 5 ngày",
            IncidentDateUtc = incidentTime
        });

        Assert.True(outcome.IsSuccess);
        // 5 ngày * 2.000 VNĐ = 10.000 VNĐ
        Assert.Equal(10_000m, outcome.OverdueFee);

        var refreshedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(refreshedLoan);
        Assert.Equal(10_000m, refreshedLoan.OverdueFee);
    }

    // -------------------------------------------------------------------------
    // TEST 10: Không tính phí trễ hạn khi phiếu mượn còn trong hạn (Phí trễ hạn = 0 VNĐ)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test10_CalculateOverdueFee_WhenLoanNotOverdue_ZeroFee()
    {
        // Phiếu mượn còn 3 ngày nữa mới đến hạn
        var incidentTime = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var incidentDate = DateOnly.FromDateTime(incidentTime);
        var dueDate = incidentDate.AddDays(3); // Chưa quá hạn
        var loanDate = incidentDate.AddDays(-7);

        var (_, copy, _, loan) = await SeedLoanAsync(loanDate: loanDate, dueDate: dueDate);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Hỏng sách trong thời gian mượn hợp lệ",
            IncidentDateUtc = incidentTime
        });

        Assert.True(outcome.IsSuccess);
        Assert.Equal(0m, outcome.OverdueFee);

        var refreshedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(refreshedLoan);
        Assert.Equal(0m, refreshedLoan.OverdueFee);
    }

    // -------------------------------------------------------------------------
    // TEST 11: Không tính phí trễ hạn khi ngày ghi nhận đúng bằng hạn trả (DueDate == incidentDate)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test11_CalculateOverdueFee_WhenIncidentRecordedOnExactDueDate_ZeroFee()
    {
        var incidentTime = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var incidentDate = DateOnly.FromDateTime(incidentTime);
        var dueDate = incidentDate; // Đúng ngày hạn trả

        var (_, copy, _, loan) = await SeedLoanAsync(dueDate: dueDate);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Báo mất đúng hạn trả",
            IncidentDateUtc = incidentTime
        });

        Assert.True(outcome.IsSuccess);
        Assert.Equal(0m, outcome.OverdueFee);

        var refreshedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(refreshedLoan);
        Assert.Equal(0m, refreshedLoan.OverdueFee);
    }

    // -------------------------------------------------------------------------
    // TEST 12: Bảo toàn phí trễ hạn cũ nếu phí tính được thấp hơn phí đã có
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test12_RetainExistingOverdueFee_WhenCalculatedFeeIsLower_PreservesHigherExistingFee()
    {
        // Phiếu đã có OverdueFee trước đó là 20.000 VNĐ
        // Ngày ghi nhận sự cố chỉ trễ 2 ngày (2 * 2.000 = 4.000 VNĐ)
        var incidentTime = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var incidentDate = DateOnly.FromDateTime(incidentTime);
        var dueDate = incidentDate.AddDays(-2);

        var (_, copy, _, loan) = await SeedLoanAsync(dueDate: dueDate, initialOverdueFee: 20_000m);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Báo mất khi đã có phí trễ hạn trước đó",
            IncidentDateUtc = incidentTime
        });

        Assert.True(outcome.IsSuccess);
        // Phải bảo toàn con số 20.000 VNĐ, không hạ xuống 4.000 VNĐ
        Assert.Equal(20_000m, outcome.OverdueFee);

        var refreshedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(refreshedLoan);
        Assert.Equal(20_000m, refreshedLoan.OverdueFee);
    }

    // -------------------------------------------------------------------------
    // TEST 13: Cập nhật phí trễ hạn mới nếu phí tính được cao hơn phí cũ
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test13_UpdateOverdueFee_WhenCalculatedFeeIsHigherThanExistingFee()
    {
        // Phiếu đã có OverdueFee là 4.000 VNĐ
        // Ngày ghi nhận sự cố trễ 6 ngày (6 * 2.000 = 12.000 VNĐ)
        var incidentTime = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var incidentDate = DateOnly.FromDateTime(incidentTime);
        var dueDate = incidentDate.AddDays(-6);

        var (_, copy, _, loan) = await SeedLoanAsync(dueDate: dueDate, initialOverdueFee: 4_000m);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Báo hỏng sau 6 ngày trễ",
            IncidentDateUtc = incidentTime
        });

        Assert.True(outcome.IsSuccess);
        // Cập nhật lên 12.000 VNĐ
        Assert.Equal(12_000m, outcome.OverdueFee);

        var refreshedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(refreshedLoan);
        Assert.Equal(12_000m, refreshedLoan.OverdueFee);
    }

    // -------------------------------------------------------------------------
    // TEST 14: Hiển thị riêng biệt Phí trễ hạn và Khoản bồi thường
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test14_DisplaysOverdueFeeAndCompensationSeparately_InOutcomeAndRecentIncidents()
    {
        await SeedCompensationPolicyAsync(lostRate: 1.5m);

        // Giá bìa 100.000đ, hệ số Mất 1.5x -> Bồi thường = 150.000đ
        // Trễ 3 ngày -> Phí trễ hạn = 3 * 2.000 = 6.000đ
        var incidentTime = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var incidentDate = DateOnly.FromDateTime(incidentTime);
        var dueDate = incidentDate.AddDays(-3);

        var (_, copy, _, loan) = await SeedLoanAsync(dueDate: dueDate, coverPrice: 100_000m);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Báo mất sách quá hạn 3 ngày",
            IncidentDateUtc = incidentTime
        });

        Assert.True(outcome.IsSuccess);
        // Hai khoản riêng biệt:
        Assert.Equal(6_000m, outcome.OverdueFee);
        Assert.NotNull(outcome.Compensation);
        Assert.Equal(150_000m, outcome.Compensation.CurrentAmount);
        Assert.NotEqual(outcome.OverdueFee, outcome.Compensation.CurrentAmount);

        // Kiểm tra danh sách sự cố gần đây: BookLoan và Compensation tải độc lập
        var recentList = await _incidentService.GetRecentIncidentsAsync(10);
        var recordedIncident = recentList.FirstOrDefault(i => i.Id == outcome.Incident!.Id);

        Assert.NotNull(recordedIncident);
        Assert.NotNull(recordedIncident.BookLoan);
        Assert.Equal(6_000m, recordedIncident.BookLoan.OverdueFee);
        Assert.Equal(BookLoanCloseReason.Lost, recordedIncident.BookLoan.CloseReason);
        Assert.NotNull(recordedIncident.Compensation);
        Assert.Equal(150_000m, recordedIncident.Compensation.CurrentAmount);
    }

    // -------------------------------------------------------------------------
    // TEST 15: Không làm tăng số nợ OutstandingBalance của tài khoản bạn đọc khi đóng phiếu
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test15_DoesNotAffectReaderOutstandingBalance_WhenClosingLoanOnIncident()
    {
        var initialBalance = 50_000m;
        var incidentTime = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var incidentDate = DateOnly.FromDateTime(incidentTime);
        var dueDate = incidentDate.AddDays(-4); // Phí trễ 8.000đ

        var (_, copy, reader, loan) = await SeedLoanAsync(
            dueDate: dueDate,
            coverPrice: 120_000m,
            initialReaderBalance: initialBalance);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Báo mất",
            IncidentDateUtc = incidentTime
        });

        Assert.True(outcome.IsSuccess);

        // OutstandingBalance của bạn đọc không đổi (theo quyết định A3 chốt với PO)
        var refreshedReader = await _db.ReaderAccounts.FindAsync(reader.Id);
        Assert.NotNull(refreshedReader);
        Assert.Equal(initialBalance, refreshedReader.OutstandingBalance);
    }

    // -------------------------------------------------------------------------
    // TEST 16: Mỗi phiếu mượn gắn với 1 bản sao, đóng phiếu sự cố không ảnh hưởng phiếu khác của bạn đọc
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test16_MultipleLoansPerReader_ClosingOneIncidentLoan_DoesNotAffectOtherLoans()
    {
        var (_, copy1, reader, loan1) = await SeedLoanAsync();

        // Tạo thêm phiếu mượn thứ 2 cho cùng bạn đọc
        var author2 = new Author { Name = "Tác giả 2" };
        var category2 = new Category { Name = "Thể loại 2" };
        _db.Authors.Add(author2);
        _db.Categories.Add(category2);
        var book2 = new Book { Title = "Sách 2", Author = author2, Category = category2 };
        _db.Books.Add(book2);
        var shelf = await _db.Shelves.FirstAsync();
        var copy2 = new BookCopy
        {
            Book = book2,
            Shelf = shelf,
            CopyCode = "CP-BOOK2",
            Status = BookCopyStatus.OnLoan,
            PhysicalCondition = BookCopyCondition.Good
        };
        _db.BookCopies.Add(copy2);
        var loan2 = new BookLoan
        {
            Book = book2,
            BookId = book2.Id,
            BookCopy = copy2,
            BookCopyId = copy2.Id,
            ReaderAccount = reader,
            ReaderAccountId = reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today),
            OriginalDueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(14)),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(14))
        };
        _db.BookLoans.Add(loan2);
        await _db.SaveChangesAsync();

        // Ghi nhận sự cố chỉ trên phiếu 1
        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan1.Id,
            BookCopyId = copy1.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Báo mất sách phiếu 1"
        });
        Assert.True(outcome.IsSuccess);

        // Phiếu 1 đã đóng
        var refreshedLoan1 = await _db.BookLoans.FindAsync(loan1.Id);
        Assert.NotNull(refreshedLoan1!.ReturnedAtUtc);
        Assert.True(refreshedLoan1.IsReturned);
        Assert.Equal(BookLoanCloseReason.Lost, refreshedLoan1.CloseReason);

        // Phiếu 2 vẫn mở bình thường
        var refreshedLoan2 = await _db.BookLoans.FindAsync(loan2.Id);
        Assert.Null(refreshedLoan2!.ReturnedAtUtc);
        Assert.False(refreshedLoan2.IsReturned);
        Assert.Null(refreshedLoan2.CloseReason);
    }

    // -------------------------------------------------------------------------
    // TEST 17: Controller MVC và API trả về đúng trạng thái đóng phiếu và tách biệt các khoản phí
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test17_ControllerMvcAndApi_ReturnAccurateClosedLoanDetailsAndSeparateFees()
    {
        await SeedCompensationPolicyAsync(lostRate: 1.2m);

        var incidentTime = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        var incidentDate = DateOnly.FromDateTime(incidentTime);
        var dueDate = incidentDate.AddDays(-4); // 4 ngày trễ -> 8.000đ

        var (_, copy, _, loan) = await SeedLoanAsync(dueDate: dueDate, coverPrice: 50_000m);

        var auditMock = new AuditLogService(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditLogService>.Instance);
        var controller = new BookIncidentController(_incidentService, auditMock, _compensationService)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new FakeTempDataProvider())
        };

        // 1. Kiểm tra Record API
        var apiResult = await controller.RecordApi(new RecordBookIncidentViewModel
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Báo mất qua API"
        }) as OkObjectResult;

        Assert.NotNull(apiResult);
        var json = System.Text.Json.JsonSerializer.Serialize(apiResult.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(loan.Id, doc.RootElement.GetProperty("loanId").GetInt64());
        Assert.Equal(BookLoanCloseReason.Lost, doc.RootElement.GetProperty("closeReason").GetString());
        Assert.True(doc.RootElement.TryGetProperty("compensation", out var compElement));
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, compElement.ValueKind);

        // 2. Kiểm tra phiếu mượn trong CSDL sau khi gọi API
        var refreshedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(refreshedLoan);
        Assert.True(refreshedLoan.IsReturned);
        Assert.Equal(BookLoanCloseReason.Lost, refreshedLoan.CloseReason);

        // 3. Kiểm tra gọi lại API trên phiếu đã đóng phải trả về lỗi
        var duplicateApiResult = await controller.RecordApi(new RecordBookIncidentViewModel
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Báo hỏng lại"
        }) as BadRequestObjectResult;

        Assert.NotNull(duplicateApiResult);
    }

    private sealed class FakeTempDataProvider : ITempDataProvider
    {
        private readonly Dictionary<string, object> _data = new();
        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;
        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            foreach (var kvp in values) _data[kvp.Key] = kvp.Value;
        }
    }
}
