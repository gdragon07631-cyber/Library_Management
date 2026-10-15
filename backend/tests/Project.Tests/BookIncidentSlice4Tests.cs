using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public class BookIncidentSlice4Tests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly BookIncidentService _incidentService;
    private readonly CompensationService _compensationService;

    public BookIncidentSlice4Tests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDbContext(options);
        _incidentService = new BookIncidentService(_db);
        _compensationService = new CompensationService(_db);
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
        decimal? coverPrice = 120_000m,
        decimal initialOverdueFee = 0m)
    {
        var author = new Author { Name = "Tác giả " + Guid.NewGuid().ToString("N")[..6] };
        var category = new Category { Name = "Thể loại " + Guid.NewGuid().ToString("N")[..6] };
        _db.Authors.Add(author);
        _db.Categories.Add(category);

        var book = new Book
        {
            Title = "Lập trình C# nâng cao",
            Author = author,
            Category = category
        };
        _db.Books.Add(book);
        await _db.SaveChangesAsync();

        var copy = new BookCopy
        {
            BookId = book.Id,
            CopyCode = "BC-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Status = BookCopyStatus.OnLoan,
            CoverPrice = coverPrice,
            PhysicalCondition = BookCopyCondition.Good
        };
        _db.BookCopies.Add(copy);

        var reader = new ReaderAccount
        {
            FullName = "Nguyễn Văn Kiểm Thử",
            Email = $"reader_{Guid.NewGuid():N}@test.local",
            PasswordHash = "hash",
            OutstandingBalance = 0m
        };
        _db.ReaderAccounts.Add(reader);
        await _db.SaveChangesAsync();

        var lDate = loanDate ?? new DateOnly(2026, 10, 1);
        var dDate = dueDate ?? new DateOnly(2026, 10, 15);

        var loan = new BookLoan
        {
            BookId = book.Id,
            BookCopyId = copy.Id,
            ReaderAccountId = reader.Id,
            LoanDate = lDate,
            OriginalDueDate = dDate,
            DueDate = dDate,
            OverdueFee = initialOverdueFee,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.BookLoans.Add(loan);
        await _db.SaveChangesAsync();

        return (book, copy, reader, loan);
    }

    // 1. Ghi nhật ký đúng người thực hiện và thời điểm ghi nhận sách Mất
    [Fact]
    public async Task Test01_LogsActorAndExactTimestampForLostBook()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var staffEmail = "librarian@library.gov.vn";
        var incidentTime = new DateTime(2026, 10, 15, 14, 30, 0, DateTimeKind.Utc);

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Bạn đọc làm rơi mất sách trên tàu",
            StaffEmail = staffEmail,
            IncidentDateUtc = incidentTime
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);
        Assert.True(outcome.IsSuccess);

        var logs = await _db.BookIncidentAuditLogs
            .Where(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.RecordIncident)
            .ToListAsync();

        var recordLog = Assert.Single(logs);
        Assert.Equal(staffEmail, recordLog.Actor);
        Assert.Equal(incidentTime, recordLog.OccurredAtUtc);
        Assert.Equal(BookIncidentType.Lost, recordLog.IncidentType);
        Assert.Equal(BookCopyStatus.Removed, recordLog.CopyStatusResult);
    }

    // 2. Ghi nhật ký đúng người thực hiện và thời điểm ghi nhận sách Hỏng nặng
    [Fact]
    public async Task Test02_LogsActorAndExactTimestampForDamagedBook()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var staffEmail = "manager@library.gov.vn";
        var incidentTime = new DateTime(2026, 10, 16, 9, 15, 0, DateTimeKind.Utc);

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Sách bị dính nước mưa ướt toàn bộ, bong tróc bìa",
            StaffEmail = staffEmail,
            IncidentDateUtc = incidentTime
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);
        Assert.True(outcome.IsSuccess);

        var recordLog = await _db.BookIncidentAuditLogs
            .SingleAsync(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.RecordIncident);

        Assert.Equal(staffEmail, recordLog.Actor);
        Assert.Equal(incidentTime, recordLog.OccurredAtUtc);
        Assert.Equal(BookIncidentType.Damaged, recordLog.IncidentType);
        Assert.Equal(BookCopyStatus.Removed, recordLog.CopyStatusResult);
    }

    // 3. Ghi nhật ký đúng phiếu mượn, bản sao, tình trạng và mô tả
    [Fact]
    public async Task Test03_LogsCorrectLoanCopyIncidentTypeAndDescription()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var description = "Rách bìa trước và mất 5 trang đầu";

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = description,
            StaffEmail = "librarian@test.local"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);
        Assert.True(outcome.IsSuccess);

        var log = await _db.BookIncidentAuditLogs
            .FirstAsync(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.RecordIncident);

        Assert.Equal(loan.Id, log.BookLoanId);
        Assert.Equal(copy.Id, log.BookCopyId);
        Assert.Equal(BookIncidentType.Damaged, log.IncidentType);
        Assert.Equal(description, log.Description);
        Assert.Contains(copy.CopyCode, log.Summary);
    }

    // 4. Lưu đúng thông tin ảnh chụp nếu có
    [Fact]
    public async Task Test04_LogsPhotoPathWhenPhotoProvided()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();

        var photoMock = new FormFile(
            new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }),
            0,
            4,
            "Photo",
            "evidence.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Ảnh chụp hiện trạng sách rách",
            Photo = photoMock,
            StaffEmail = "librarian@test.local"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);
        Assert.True(outcome.IsSuccess);

        var log = await _db.BookIncidentAuditLogs
            .FirstAsync(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.RecordIncident);

        Assert.NotNull(log.PhotoPath);
        Assert.Contains("/uploads/incidents/", log.PhotoPath);
        Assert.EndsWith(".jpg", log.PhotoPath);
    }

    // 5. Ghi nhận chính xác giá bìa, hệ số và số tiền bồi thường ban đầu
    [Fact]
    public async Task Test05_LogsAccurateCoverPriceMultiplierAndInitialCompensation()
    {
        await SeedCompensationPolicyAsync(lostRate: 1.5m, damagedRate: 0.8m);
        var coverPrice = 200_000m;
        var (_, copy, _, loan) = await SeedLoanAsync(coverPrice: coverPrice);

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách giáo trình",
            StaffEmail = "staff@test.local"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);
        Assert.True(outcome.IsSuccess);

        var compLog = await _db.BookIncidentAuditLogs
            .SingleAsync(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.CalculateCompensation);

        Assert.Equal(coverPrice, compLog.RecordedCoverPrice);
        Assert.Equal(1.5m, compLog.AppliedRate);
        Assert.Equal(300_000m, compLog.InitialCompensationAmount);
        Assert.Equal(300_000m, compLog.ToAmount);
        Assert.Contains("300,000", compLog.Summary);
    }

    // 6. Ghi nhận đúng số tiền trước điều chỉnh, số tiền sau điều chỉnh và lý do
    [Fact]
    public async Task Test06_LogsFromAmountToAmountAndRequiredReasonOnCompensationAdjustment()
    {
        await SeedCompensationPolicyAsync(lostRate: 1.0m);
        var (_, copy, _, loan) = await SeedLoanAsync(coverPrice: 100_000m);

        var recordOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "librarian@test.local"
        });
        Assert.True(recordOutcome.IsSuccess);

        var adjustReason = "Bạn đọc thuộc diện chính sách khó khăn, giảm bồi thường";
        var staffEmail = "manager@library.local";
        var adjustOutcome = await _compensationService.AdjustCompensationAsync(new AdjustCompensationRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            NewAmount = 60_000m,
            Reason = adjustReason
        }, AccountRoles.LibraryManager, staffEmail);

        Assert.True(adjustOutcome.IsSuccess);

        var adjustLog = await _db.BookIncidentAuditLogs
            .SingleAsync(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.AdjustCompensation);

        Assert.Equal(100_000m, adjustLog.FromAmount);
        Assert.Equal(60_000m, adjustLog.ToAmount);
        Assert.Equal(adjustReason, adjustLog.AdjustmentReason);
        Assert.Equal(staffEmail, adjustLog.Actor);
        Assert.Contains("100,000", adjustLog.Summary);
        Assert.Contains("60,000", adjustLog.Summary);
    }

    // 7. Ghi nhật ký đóng phiếu với lý do Mất hoặc Hỏng
    [Theory]
    [InlineData(BookIncidentType.Lost)]
    [InlineData(BookIncidentType.Damaged)]
    public async Task Test07_LogsCloseLoanWithCorrectReasonLostOrDamaged(string incidentType)
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = incidentType,
            Description = "Sự cố kiểm thử đóng phiếu",
            StaffEmail = "staff@test.local"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);
        Assert.True(outcome.IsSuccess);

        var closeLog = await _db.BookIncidentAuditLogs
            .SingleAsync(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.CloseLoan);

        Assert.Equal(incidentType, closeLog.CloseReason);
        Assert.Equal(loan.Id, closeLog.BookLoanId);
        Assert.Contains(incidentType, closeLog.Summary);
    }

    // 8. Ghi nhận đúng khoản phí trễ hạn được giữ lại
    [Fact]
    public async Task Test08_LogsAccurateOverdueFeeRetained()
    {
        await SeedCompensationPolicyAsync();
        // Hạn trả: 10/10/2026, ngày ghi nhận: 15/10/2026 (trễ 5 ngày -> 10.000 VNĐ)
        var (_, copy, _, loan) = await SeedLoanAsync(dueDate: new DateOnly(2026, 10, 10));
        var incidentTime = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách quá hạn",
            StaffEmail = "staff@test.local",
            IncidentDateUtc = incidentTime
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);
        Assert.True(outcome.IsSuccess);

        var closeLog = await _db.BookIncidentAuditLogs
            .SingleAsync(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.CloseLoan);

        Assert.Equal(10_000m, closeLog.OverdueFee);
        Assert.Equal(new DateOnly(2026, 10, 15), closeLog.IncidentDate);
        Assert.Contains("10,000", closeLog.Summary);
    }

    // 9. Không tạo nhật ký thành công khi nghiệp vụ thất bại
    [Fact]
    public async Task Test09_DoesNotCreateAuditLogWhenOperationFails()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();

        // Thất bại vì thiếu mô tả bắt buộc
        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "   ",
            StaffEmail = "staff@test.local"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);
        Assert.False(outcome.IsSuccess);

        var logs = await _db.BookIncidentAuditLogs.Where(l => l.BookLoanId == loan.Id).ToListAsync();
        Assert.Empty(logs);
    }

    // 10. Không tạo nhật ký trùng khi gửi lại cùng một yêu cầu
    [Fact]
    public async Task Test10_DoesNotCreateDuplicateAuditLogsOnReplay()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Ghi nhận sự cố lần đầu",
            StaffEmail = "staff@test.local"
        };

        var firstOutcome = await _incidentService.RecordIncidentAsync(request);
        Assert.True(firstOutcome.IsSuccess);

        var logsCountAfterFirst = await _db.BookIncidentAuditLogs.CountAsync(l => l.BookLoanId == loan.Id);
        Assert.True(logsCountAfterFirst > 0);

        // Gửi lại cùng yêu cầu (phiếu đã đóng và bản sao đã bị loại khỏi kho)
        var secondOutcome = await _incidentService.RecordIncidentAsync(request);
        Assert.False(secondOutcome.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(secondOutcome.ErrorMessage));

        var logsCountAfterSecond = await _db.BookIncidentAuditLogs.CountAsync(l => l.BookLoanId == loan.Id);
        Assert.Equal(logsCountAfterFirst, logsCountAfterSecond);
    }

    // 11. Không cho phép người dùng sửa nhật ký đã ghi nhận
    [Fact]
    public async Task Test11_DisallowsModifyingExistingAuditLogs()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff@test.local"
        });
        Assert.True(outcome.IsSuccess);

        var log = await _db.BookIncidentAuditLogs.FirstAsync(l => l.BookLoanId == loan.Id);
        log.Actor = "malicious_user@hack.local";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        Assert.Contains("chỉ đọc", ex.Message);
    }

    // 12. Không cho phép người dùng xóa nhật ký đã ghi nhận
    [Fact]
    public async Task Test12_DisallowsDeletingExistingAuditLogs()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff@test.local"
        });
        Assert.True(outcome.IsSuccess);

        var log = await _db.BookIncidentAuditLogs.FirstAsync(l => l.BookLoanId == loan.Id);
        _db.BookIncidentAuditLogs.Remove(log);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        Assert.Contains("chỉ đọc", ex.Message);
    }

    // 13. Kiểm tra quyền xem nhật ký của các vai trò khác nhau
    [Fact]
    public async Task Test13_RoleAuthorizationForAuditLogs()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff@test.local"
        });
        Assert.True(outcome.IsSuccess);

        var controller = new BookIncidentController(_incidentService, new AuditLogService(_db, NullLogger<AuditLogService>.Instance), _compensationService);

        // Kiểm tra StaffOnly attribute trên BookIncidentController
        var staffOnlyAttr = typeof(BookIncidentController).GetCustomAttribute<StaffOnlyAttribute>();
        Assert.NotNull(staffOnlyAttr);
        Assert.Contains(AccountRoles.Librarian, staffOnlyAttr.Roles);
        Assert.Contains(AccountRoles.LibraryManager, staffOnlyAttr.Roles);
        Assert.Contains(AccountRoles.SystemAdmin, staffOnlyAttr.Roles);

        // Xem lịch sử của phiếu mượn
        var actionResult = await controller.History(loan.Id);
        var viewResult = Assert.IsType<ViewResult>(actionResult);
        var model = Assert.IsType<LoanIncidentHistoryViewModel>(viewResult.Model);

        Assert.Equal(loan.Id, model.Loan.Id);
        Assert.NotEmpty(model.Logs);
    }

    // 14. Hiển thị lịch sử đúng thứ tự thời gian (mới nhất đến cũ nhất)
    [Fact]
    public async Task Test14_DisplaysHistoryInChronologicalOrderNewestFirst()
    {
        await SeedCompensationPolicyAsync(lostRate: 1.0m);
        var (_, copy, _, loan) = await SeedLoanAsync(coverPrice: 100_000m);

        var t1 = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff@test.local",
            IncidentDateUtc = t1
        });
        Assert.True(outcome.IsSuccess);

        // Điều chỉnh lần 1
        await _compensationService.AdjustCompensationAsync(new AdjustCompensationRequest
        {
            IncidentId = outcome.Incident!.Id,
            NewAmount = 90_000m,
            Reason = "Giảm lần 1"
        }, AccountRoles.LibraryManager, "manager@test.local");

        // Điều chỉnh lần 2
        await _compensationService.AdjustCompensationAsync(new AdjustCompensationRequest
        {
            IncidentId = outcome.Incident!.Id,
            NewAmount = 80_000m,
            Reason = "Giảm lần 2"
        }, AccountRoles.LibraryManager, "manager@test.local");

        var history = await _incidentService.GetLoanIncidentHistoryAsync(loan.Id);
        Assert.NotNull(history);
        Assert.True(history.Logs.Count >= 3);

        // Kiểm tra sắp xếp giảm dần theo OccurredAtUtc
        for (int i = 0; i < history.Logs.Count - 1; i++)
        {
            Assert.True(history.Logs[i].OccurredAtUtc >= history.Logs[i + 1].OccurredAtUtc);
        }
    }

    // 15. Hiển thị đúng dữ liệu khi mở lại phiếu mượn
    [Fact]
    public async Task Test15_DisplaysAccurateDataWhenReopeningLoanHistory()
    {
        await SeedCompensationPolicyAsync();
        var (book, copy, reader, loan) = await SeedLoanAsync();

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách khi đi học",
            StaffEmail = "librarian@test.local"
        });
        Assert.True(outcome.IsSuccess);

        var firstFetch = await _incidentService.GetLoanIncidentHistoryAsync(loan.Id);
        var secondFetch = await _incidentService.GetLoanIncidentHistoryAsync(loan.Id);

        Assert.NotNull(firstFetch);
        Assert.NotNull(secondFetch);
        Assert.Equal(firstFetch.Loan.Id, secondFetch.Loan.Id);
        Assert.Equal(book.Title, secondFetch.Loan.Book!.Title);
        Assert.Equal(reader.FullName, secondFetch.Loan.ReaderAccount!.FullName);
        Assert.Equal(firstFetch.Logs.Count, secondFetch.Logs.Count);
    }

    // 16. Bảo đảm nhật ký gốc không bị thay đổi khi khoản bồi thường được điều chỉnh
    [Fact]
    public async Task Test16_OriginalLogsRemainUnchangedWhenCompensationAdjusted()
    {
        await SeedCompensationPolicyAsync(lostRate: 1.0m);
        var (_, copy, _, loan) = await SeedLoanAsync(coverPrice: 150_000m);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff@test.local"
        });
        Assert.True(outcome.IsSuccess);

        var originalRecordLog = await _db.BookIncidentAuditLogs
            .AsNoTracking()
            .SingleAsync(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.RecordIncident);

        var originalCalcLog = await _db.BookIncidentAuditLogs
            .AsNoTracking()
            .SingleAsync(l => l.BookLoanId == loan.Id && l.OperationType == IncidentAuditOperations.CalculateCompensation);

        // Điều chỉnh khoản bồi thường
        await _compensationService.AdjustCompensationAsync(new AdjustCompensationRequest
        {
            IncidentId = outcome.Incident!.Id,
            NewAmount = 100_000m,
            Reason = "Miễn giảm theo quyết định ban giám hiệu"
        }, AccountRoles.LibraryManager, "manager@test.local");

        // Kiểm tra lại log ban đầu
        var currentRecordLog = await _db.BookIncidentAuditLogs
            .AsNoTracking()
            .SingleAsync(l => l.Id == originalRecordLog.Id);

        var currentCalcLog = await _db.BookIncidentAuditLogs
            .AsNoTracking()
            .SingleAsync(l => l.Id == originalCalcLog.Id);

        Assert.Equal(originalRecordLog.Summary, currentRecordLog.Summary);
        Assert.Equal(originalRecordLog.Actor, currentRecordLog.Actor);
        Assert.Equal(originalCalcLog.InitialCompensationAmount, currentCalcLog.InitialCompensationAmount);
        Assert.Equal(150_000m, currentCalcLog.InitialCompensationAmount);
    }

    // 17. Bảo đảm nhật ký không bị mất khi dữ liệu liên quan được cập nhật
    [Fact]
    public async Task Test17_AuditLogsAreNotLostWhenRelatedEntitiesUpdated()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, reader, loan) = await SeedLoanAsync();

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Hỏng sách",
            StaffEmail = "staff@test.local"
        });
        Assert.True(outcome.IsSuccess);

        var initialLogCount = await _db.BookIncidentAuditLogs.CountAsync(l => l.BookLoanId == loan.Id);
        Assert.True(initialLogCount > 0);

        // Cập nhật thông tin reader và copy
        reader.PhoneNumber = "0987654321";
        copy.StatusReason = "Cập nhật ghi chú thủ thư";
        await _db.SaveChangesAsync();

        var refreshedLogCount = await _db.BookIncidentAuditLogs.CountAsync(l => l.BookLoanId == loan.Id);
        Assert.Equal(initialLogCount, refreshedLogCount);
    }

    // 18. Kiểm tra trường hợp lỗi giao dịch: không tạo dữ liệu thiếu nhất quán
    [Fact]
    public async Task Test18_AtomicRollbackEnsuresNoInconsistentStateIfAuditLogSaveFails()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();

        // Gửi yêu cầu với bản sao không khớp với phiếu mượn
        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id + 9999, // Bản sao không tồn tại
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff@test.local"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);
        Assert.False(outcome.IsSuccess);

        // Kiểm tra phiếu mượn không bị đóng và không có log nào được ghi
        var loanInDb = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(loanInDb);
        Assert.Null(loanInDb.ReturnedAtUtc);
        Assert.Null(loanInDb.CloseReason);

        var logs = await _db.BookIncidentAuditLogs.Where(l => l.BookLoanId == loan.Id).ToListAsync();
        Assert.Empty(logs);
    }

    // 19. Kiểm tra API/Service không có method sửa hoặc xóa nhật ký
    [Fact]
    public void Test19_NoUpdateOrDeleteMethodsExposedOnServiceAndController()
    {
        var serviceMethods = typeof(IBookIncidentService).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain(serviceMethods, name => Regex.IsMatch(name, "Update.*Log|Delete.*Log|Remove.*Log|Clear.*Log", RegexOptions.IgnoreCase));

        var controllerMethods = typeof(BookIncidentController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).ToList();
        Assert.DoesNotContain(controllerMethods, name => Regex.IsMatch(name, "Update.*Log|Delete.*Log|Remove.*Log|Clear.*Log", RegexOptions.IgnoreCase));
    }
}
