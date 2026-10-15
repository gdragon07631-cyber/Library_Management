using System.Security.Claims;
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

public class BookIncidentSlice2Tests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly CompensationService _compensationService;
    private readonly BookIncidentService _incidentService;
    private readonly string _testUploadsDir;

    public BookIncidentSlice2Tests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDbContext(options);
        _compensationService = new CompensationService(_db);
        _incidentService = new BookIncidentService(_db);

        _testUploadsDir = Path.Combine(Path.GetTempPath(), "library_incident_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testUploadsDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_testUploadsDir))
        {
            try { Directory.Delete(_testUploadsDir, true); } catch { /* ignore */ }
        }
    }

    private async Task<(Book Book, BookCopy Copy, ReaderAccount Reader, BookLoan Loan)> SeedOpenLoanAsync(
        decimal? coverPrice = 120000m,
        string copyStatus = BookCopyStatus.OnLoan,
        string physicalCondition = BookCopyCondition.Good)
    {
        if (!await _db.CompensationPolicies.AnyAsync())
        {
            _db.CompensationPolicies.Add(new CompensationPolicy
            {
                Id = CompensationPolicy.SingletonId,
                DefaultRate = 1.0m,
                LostRate = 1.0m,
                DamagedRate = 1.0m,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedBy = "system"
            });
        }

        var author = new Author { Name = "Tác giả " + Guid.NewGuid().ToString("N")[..6] };
        var category = new Category { Name = "Thể loại " + Guid.NewGuid().ToString("N")[..6] };
        _db.Authors.Add(author);
        _db.Categories.Add(category);

        var book = new Book
        {
            Title = "Kỹ nghệ phần mềm",
            Author = author,
            Category = category
        };
        _db.Books.Add(book);

        var warehouse = new Warehouse { Code = "KHO-" + Guid.NewGuid().ToString("N")[..4], Name = "Kho trung tâm" };
        _db.Warehouses.Add(warehouse);

        var shelf = new Shelf { Code = "KE-" + Guid.NewGuid().ToString("N")[..4], Name = "Kệ B2", Warehouse = warehouse };
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
            FullName = "Nguyễn Văn Đọc",
            Email = $"reader_{Guid.NewGuid():N}@example.com",
            StudentOrStaffCode = "SV" + Guid.NewGuid().ToString("N")[..6],
            PhoneNumber = "0901234567",
            PasswordHash = "hash",
            Status = "Đang hoạt động"
        };
        _db.ReaderAccounts.Add(reader);

        var card = new LibraryCard
        {
            CardCode = "CARD-" + Guid.NewGuid().ToString("N")[..6],
            ReaderAccount = reader,
            LibraryCardType = cardType,
            IssuedOn = DateOnly.FromDateTime(DateTime.Today),
            ExpiresOn = DateOnly.FromDateTime(DateTime.Today.AddYears(1)),
            Status = "Đang hoạt động"
        };
        _db.LibraryCards.Add(card);

        var loan = new BookLoan
        {
            BookCopy = copy,
            ReaderAccount = reader,
            LoanDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-5)),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(10)),
            ReturnedAtUtc = null,
            IsReturned = false
        };
        _db.BookLoans.Add(loan);

        await _db.SaveChangesAsync();
        return (book, copy, reader, loan);
    }

    private static BookIncidentController CreateController(ApplicationDbContext db, ICompensationService compensationService, string userRole, string username = "staff1")
    {
        var incidentService = new BookIncidentService(db);
        var auditService = new AuditLogService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditLogService>.Instance);
        var controller = new BookIncidentController(incidentService, auditService, compensationService);

        var httpContext = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, userRole),
            new Claim(ClaimTypes.Email, $"{username}@library.vn")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        httpContext.User = new ClaimsPrincipal(identity);

        controller.TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider());
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private static CompensationPolicyController CreatePolicyController(ApplicationDbContext db, ICompensationService compensationService, string userRole, string username = "manager1")
    {
        var auditService = new AuditLogService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditLogService>.Instance);
        var controller = new CompensationPolicyController(compensationService, auditService);

        var httpContext = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, userRole),
            new Claim(ClaimTypes.Email, $"{username}@library.vn")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        httpContext.User = new ClaimsPrincipal(identity);

        controller.TempData = new TempDataDictionary(httpContext, new FakeTempDataProvider());
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    // -------------------------------------------------------------------------
    // TEST 1: Manager sets valid rate successfully
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test1_Manager_SetsValidRate_Successfully()
    {
        var outcome = await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest
            {
                DefaultRate = 1.5m,
                LostRate = 2.0m,
                DamagedRate = 1.2m
            },
            staffRole: AccountRoles.LibraryManager,
            staffEmail: "manager@library.vn");

        Assert.True(outcome.IsSuccess);
        Assert.NotNull(outcome.Policy);
        Assert.Equal(1.5m, outcome.Policy.DefaultRate);
        Assert.Equal(2.0m, outcome.Policy.LostRate);
        Assert.Equal(1.2m, outcome.Policy.DamagedRate);

        var saved = await _compensationService.GetPolicyAsync();
        Assert.NotNull(saved);
        Assert.Equal(1.5m, saved.DefaultRate);
        Assert.Equal(2.0m, saved.LostRate);
        Assert.Equal(1.2m, saved.DamagedRate);
        Assert.Equal("manager@library.vn", saved.UpdatedBy);
    }

    // -------------------------------------------------------------------------
    // TEST 2: Manager updates rate successfully
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test2_Manager_UpdatesExistingRate_Successfully()
    {
        // Initial setup
        await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest { DefaultRate = 1.0m, LostRate = 1.0m },
            AccountRoles.LibraryManager, "admin1");

        // Update
        var updateOutcome = await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest
            {
                DefaultRate = 1.8m,
                LostRate = 2.5m,
                DamagedRate = 1.5m
            },
            AccountRoles.SystemAdmin, "admin2");

        Assert.True(updateOutcome.IsSuccess);
        var policy = await _compensationService.GetPolicyAsync();
        Assert.NotNull(policy);
        Assert.Equal(1.8m, policy.DefaultRate);
        Assert.Equal(2.5m, policy.LostRate);
        Assert.Equal(1.5m, policy.DamagedRate);
        Assert.Equal("admin2", policy.UpdatedBy);
    }

    // -------------------------------------------------------------------------
    // TEST 3: Non-manager (Librarian/Reader) cannot update rate
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test3_NonManager_CannotUpdateRate()
    {
        // Set initial baseline
        await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest { DefaultRate = 1.0m },
            AccountRoles.LibraryManager, "manager");

        // Attempt as Librarian
        var librarianOutcome = await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest { DefaultRate = 3.0m },
            AccountRoles.Librarian, "librarian");

        Assert.False(librarianOutcome.IsSuccess);
        Assert.Contains("quyền", librarianOutcome.ErrorMessage);

        // Attempt as Reader
        var readerOutcome = await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest { DefaultRate = 0.5m },
            "Reader", "reader1");

        Assert.False(readerOutcome.IsSuccess);

        // Verify policy in DB was NOT modified
        var policy = await _compensationService.GetPolicyAsync();
        Assert.NotNull(policy);
        Assert.Equal(1.0m, policy.DefaultRate);
    }

    // -------------------------------------------------------------------------
    // TEST 4: Accurate calculation across multiple cover prices and multipliers
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(100000, 1.5, 150000)]
    [InlineData(120500, 1.25, 150625)]
    [InlineData(89900, 2.0, 179800)]
    [InlineData(49999.99, 1.33, 66499.99)]
    [InlineData(250000, 1.0, 250000)]
    public async Task Test4_AccurateCalculation_AcrossPricesAndRates(
        decimal coverPrice, decimal defaultRate, decimal expectedAmount)
    {
        await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest { DefaultRate = defaultRate },
            AccountRoles.LibraryManager, "manager");

        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: coverPrice);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Kiểm tra tính chính xác bồi thường",
            StaffEmail = "tester"
        });

        Assert.True(outcome.IsSuccess);
        Assert.NotNull(outcome.Compensation);
        Assert.Equal(expectedAmount, outcome.Compensation.InitialAmount);
        Assert.Equal(expectedAmount, outcome.Compensation.CurrentAmount);
        Assert.Equal(coverPrice, outcome.Compensation.RecordedCoverPrice);
        Assert.Equal(defaultRate, outcome.Compensation.AppliedRate);
    }

    // -------------------------------------------------------------------------
    // TEST 5: Exact cover price & rate used at incident time (snapshot)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test5_ExactCoverPriceAndRateUsedAtIncidentTime_Snapshot()
    {
        await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest
            {
                DefaultRate = 1.0m,
                LostRate = 1.5m,
                DamagedRate = 1.2m
            },
            AccountRoles.LibraryManager, "manager");

        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 200000m);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách có snapshot",
            StaffEmail = "tester"
        });

        Assert.True(outcome.IsSuccess);
        var comp = outcome.Compensation!;
        Assert.Equal(200000m, comp.RecordedCoverPrice);
        Assert.Equal(1.5m, comp.AppliedRate);
        Assert.Equal(300000m, comp.InitialAmount);
        Assert.Equal(300000m, comp.CurrentAmount);
    }

    // -------------------------------------------------------------------------
    // TEST 6: Safe handling when cover price is missing (no auto-compensation, no dummy 0)
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-50000.0)]
    public async Task Test6_MissingCoverPrice_DoesNotCreateFalseCompensation(double? missingPriceDouble)
    {
        decimal? missingPrice = missingPriceDouble.HasValue ? (decimal)missingPriceDouble.Value : null;

        await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest { DefaultRate = 1.5m },
            AccountRoles.LibraryManager, "manager");

        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: missingPrice);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Sách không có giá bìa",
            StaffEmail = "tester"
        });

        // Incident recording MUST still succeed (Slice 1 requirement)
        Assert.True(outcome.IsSuccess);
        Assert.NotNull(outcome.Incident);

        // Copy MUST still be Removed
        var refreshedCopy = await _db.BookCopies.FindAsync(copy.Id);
        Assert.Equal(BookCopyStatus.Removed, refreshedCopy!.Status);

        // Compensation MUST NOT be created with dummy values
        Assert.Null(outcome.Compensation);
        Assert.Contains("chưa có giá bìa", outcome.CompensationMessage);

        var inDbComp = await _db.BookIncidentCompensations
            .FirstOrDefaultAsync(c => c.BookIncidentId == outcome.Incident.Id);
        Assert.Null(inDbComp);
    }

    // -------------------------------------------------------------------------
    // TEST 7: Safe handling when policy is missing (no false compensation generated)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test7_SafeHandling_WhenPolicyNotExplicitlyConfigured()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 100000m);

        // Explicitly clear any existing policies to simulate unconfigured policy
        _db.CompensationPolicies.RemoveRange(_db.CompensationPolicies);
        await _db.SaveChangesAsync();

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Kiểm tra khi chưa có chính sách bồi thường",
            StaffEmail = "tester"
        });

        // Incident recording MUST still succeed
        Assert.True(outcome.IsSuccess);
        // But compensation is NOT automatically calculated with arbitrary values
        Assert.Null(outcome.Compensation);
        Assert.Contains("chưa thiết lập hệ số", outcome.CompensationMessage);
    }

    // -------------------------------------------------------------------------
    // TEST 8: Invalid price/rate rejects policy update
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1.5)]
    public async Task Test8_InvalidRate_RejectsPolicyUpdate(decimal invalidRate)
    {
        var outcome = await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest { DefaultRate = invalidRate },
            AccountRoles.LibraryManager, "manager");

        Assert.False(outcome.IsSuccess);
        Assert.Contains("lớn hơn 0", outcome.ErrorMessage);
    }

    // -------------------------------------------------------------------------
    // TEST 9: Valid adjustment succeeds with new amount & reason
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test9_ValidAdjustment_SucceedsWithNewAmountAndReason()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 200000m);
        var incidentOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Sách mất cần điều chỉnh",
            StaffEmail = "staff1"
        });

        Assert.True(incidentOutcome.IsSuccess);
        var incidentId = incidentOutcome.Incident!.Id;

        // Staff adjusts amount to 150,000 VNĐ
        var adjustOutcome = await _compensationService.AdjustCompensationAsync(
            new AdjustCompensationRequest
            {
                IncidentId = incidentId,
                NewAmount = 150000m,
                Reason = "Bạn đọc có hoàn cảnh khó khăn, hỗ trợ giảm trừ 50k"
            },
            staffRole: AccountRoles.Librarian,
            staffEmail: "librarian1");

        Assert.True(adjustOutcome.IsSuccess);
        Assert.NotNull(adjustOutcome.Compensation);
        Assert.Equal(200000m, adjustOutcome.Compensation.InitialAmount);
        Assert.Equal(150000m, adjustOutcome.Compensation.CurrentAmount);
        Assert.True(adjustOutcome.Compensation.IsAdjusted);
        Assert.Equal("Bạn đọc có hoàn cảnh khó khăn, hỗ trợ giảm trừ 50k", adjustOutcome.Compensation.AdjustmentReason);
        Assert.Equal("librarian1", adjustOutcome.Compensation.AdjustedBy);

        // Check history table
        var history = await _db.CompensationAdjustmentHistories
            .Where(h => h.BookIncidentCompensationId == adjustOutcome.Compensation.Id)
            .ToListAsync();
        Assert.Single(history);
        Assert.Equal(200000m, history[0].FromAmount);
        Assert.Equal(150000m, history[0].ToAmount);
        Assert.Equal("librarian1", history[0].AdjustedBy);
    }

    // -------------------------------------------------------------------------
    // TEST 10: Missing adjustment reason is rejected
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Test10_MissingAdjustmentReason_IsRejected(string? emptyReason)
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 100000m);
        var incidentOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff1"
        });

        var adjustOutcome = await _compensationService.AdjustCompensationAsync(
            new AdjustCompensationRequest
            {
                IncidentId = incidentOutcome.Incident!.Id,
                NewAmount = 80000m,
                Reason = emptyReason!
            },
            staffRole: AccountRoles.Librarian,
            staffEmail: "librarian1");

        Assert.False(adjustOutcome.IsSuccess);
        Assert.Contains("lý do", adjustOutcome.ErrorMessage!.ToLowerInvariant());

        // Verify amount in DB was NOT changed
        var comp = await _db.BookIncidentCompensations
            .FirstAsync(c => c.BookIncidentId == incidentOutcome.Incident.Id);
        Assert.Equal(100000m, comp.CurrentAmount);
        Assert.False(comp.IsAdjusted);
    }

    // -------------------------------------------------------------------------
    // TEST 11: Negative adjustment amount rejected
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(-1)]
    [InlineData(-50000)]
    public async Task Test11_NegativeAdjustmentAmount_IsRejected(decimal negativeAmount)
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 100000m);
        var incidentOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff1"
        });

        var adjustOutcome = await _compensationService.AdjustCompensationAsync(
            new AdjustCompensationRequest
            {
                IncidentId = incidentOutcome.Incident!.Id,
                NewAmount = negativeAmount,
                Reason = "Lý do hợp lệ nhưng số tiền âm"
            },
            staffRole: AccountRoles.Librarian,
            staffEmail: "librarian1");

        Assert.False(adjustOutcome.IsSuccess);
        Assert.Contains("nhỏ hơn 0", adjustOutcome.ErrorMessage);
    }

    // -------------------------------------------------------------------------
    // TEST 12: Unauthorized user cannot adjust
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test12_UnauthorizedUser_CannotAdjustCompensation()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 100000m);
        var incidentOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff1"
        });

        // Reader attempting to adjust compensation
        var adjustOutcome = await _compensationService.AdjustCompensationAsync(
            new AdjustCompensationRequest
            {
                IncidentId = incidentOutcome.Incident!.Id,
                NewAmount = 0m,
                Reason = "Bạn đọc tự giảm nợ"
            },
            staffRole: "Reader",
            staffEmail: "reader1");

        Assert.False(adjustOutcome.IsSuccess);
        Assert.Contains("quyền", adjustOutcome.ErrorMessage!.ToLowerInvariant());

        var comp = await _db.BookIncidentCompensations
            .FirstAsync(c => c.BookIncidentId == incidentOutcome.Incident.Id);
        Assert.Equal(100000m, comp.CurrentAmount);
    }

    // -------------------------------------------------------------------------
    // TEST 13: Initial amount, current amount, and reason persisted and displayed
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test13_InitialAndCurrentAmountAndReason_PersistedAndDisplayed()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 200000m);
        var incidentOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách",
            StaffEmail = "staff1"
        });

        var incidentId = incidentOutcome.Incident!.Id;
        await _compensationService.AdjustCompensationAsync(
            new AdjustCompensationRequest
            {
                IncidentId = incidentId,
                NewAmount = 180000m,
                Reason = "Điều chỉnh lần 1: Giảm 20k"
            },
            AccountRoles.Librarian, "staff1");

        await _compensationService.AdjustCompensationAsync(
            new AdjustCompensationRequest
            {
                IncidentId = incidentId,
                NewAmount = 150000m,
                Reason = "Điều chỉnh lần 2: Giảm thêm 30k"
            },
            AccountRoles.LibraryManager, "manager1");

        var comp = await _db.BookIncidentCompensations
            .Include(c => c.AdjustmentHistories)
            .FirstAsync(c => c.BookIncidentId == incidentId);

        Assert.Equal(200000m, comp.InitialAmount);
        Assert.Equal(150000m, comp.CurrentAmount);
        Assert.True(comp.IsAdjusted);
        Assert.Equal("Điều chỉnh lần 2: Giảm thêm 30k", comp.AdjustmentReason);
        Assert.Equal("manager1", comp.AdjustedBy);
        Assert.Equal(2, comp.AdjustmentHistories.Count);
    }

    // -------------------------------------------------------------------------
    // TEST 14: Duplicate compensation prevention on multiple requests
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test14_DuplicateCompensationPrevention_OnMultipleRequests()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 150000m);
        var incidentOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Ghi nhận sự cố",
            StaffEmail = "staff1"
        });

        Assert.True(incidentOutcome.IsSuccess);
        var incidentId = incidentOutcome.Incident!.Id;

        // Try to trigger compensation calculation again for the same incident
        var duplicateComp = await _compensationService.CalculateAndCreateCompensationAsync(incidentId);

        // Should return failure indicating it was already calculated
        Assert.False(duplicateComp.IsSuccess);
        Assert.Contains("trước đó", duplicateComp.ErrorMessage);

        // DB still has exactly 1 compensation record
        var totalComps = await _db.BookIncidentCompensations
            .Where(c => c.BookIncidentId == incidentId)
            .CountAsync();
        Assert.Equal(1, totalComps);
    }

    // -------------------------------------------------------------------------
    // TEST 15: Changing policy or book copy cover price later does NOT alter existing compensation
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test15_ChangingPolicyOrCopyCoverPriceLater_DoesNotAlterExistingCompensation()
    {
        // 1. Initial policy = 1.5x
        await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest { DefaultRate = 1.5m },
            AccountRoles.LibraryManager, "manager");

        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 100000m);

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Snapshot test",
            StaffEmail = "staff1"
        });

        Assert.True(outcome.IsSuccess);
        Assert.Equal(150000m, outcome.Compensation!.InitialAmount);
        Assert.Equal(150000m, outcome.Compensation.CurrentAmount);
        Assert.Equal(1.5m, outcome.Compensation.AppliedRate);
        Assert.Equal(100000m, outcome.Compensation.RecordedCoverPrice);

        // 2. Change policy to 3.0x
        await _compensationService.UpdatePolicyAsync(
            new UpdateCompensationPolicyRequest { DefaultRate = 3.0m },
            AccountRoles.LibraryManager, "manager");

        // 3. Mutate copy cover price in database to 999999m
        copy.CoverPrice = 999999m;
        await _db.SaveChangesAsync();

        // 4. Verify snapshot on recorded incident is UNTOUCHED
        var existingComp = await _db.BookIncidentCompensations
            .FirstAsync(c => c.BookIncidentId == outcome.Incident!.Id);

        Assert.Equal(150000m, existingComp.InitialAmount);
        Assert.Equal(150000m, existingComp.CurrentAmount);
        Assert.Equal(1.5m, existingComp.AppliedRate);
        Assert.Equal(100000m, existingComp.RecordedCoverPrice);
    }

    // -------------------------------------------------------------------------
    // TEST 16: Adjusting compensation does NOT mutate copy status or close loan
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test16_AdjustingCompensation_DoesNotMutateCopyStatusOrCloseLoan()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 120000m);
        var incidentOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Kiểm tra bất biến phiếu mượn",
            StaffEmail = "staff1"
        });

        var incidentId = incidentOutcome.Incident!.Id;

        // Perform adjustment
        var adjustOutcome = await _compensationService.AdjustCompensationAsync(
            new AdjustCompensationRequest
            {
                IncidentId = incidentId,
                NewAmount = 60000m,
                Reason = "Giảm 50%"
            },
            staffRole: AccountRoles.Librarian,
            staffEmail: "staff1");

        Assert.True(adjustOutcome.IsSuccess);

        // Verify copy status remains Removed
        var refreshedCopy = await _db.BookCopies.FindAsync(copy.Id);
        Assert.Equal(BookCopyStatus.Removed, refreshedCopy!.Status);

        // Verify loan is closed (Lát 3)
        var refreshedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(refreshedLoan!.ReturnedAtUtc);
        Assert.True(refreshedLoan.IsReturned);
        Assert.Equal(BookIncidentType.Lost, refreshedLoan.CloseReason);
    }

    // -------------------------------------------------------------------------
    // TEST 17: Controller MVC actions and API endpoints integration
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Test17_ControllerIntegration_AdjustCompensationAndPolicy()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(coverPrice: 100000m);
        var incidentOutcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Kiểm tra Controller",
            StaffEmail = "staff1"
        });

        var incidentId = incidentOutcome.Incident!.Id;

        // 1. Policy Controller (Manager)
        var policyController = CreatePolicyController(_db, _compensationService, AccountRoles.LibraryManager);
        var updateResult = await policyController.Update(new CompensationPolicyViewModel
        {
            DefaultRate = 2.0m,
            LostRate = 2.5m,
            DamagedRate = 1.8m
        });
        var redirect = Assert.IsType<RedirectToActionResult>(updateResult);
        Assert.Equal("Index", redirect.ActionName);

        // 2. Incident Controller Adjust (Librarian)
        var incidentController = CreateController(_db, _compensationService, AccountRoles.Librarian);
        var adjustResult = await incidentController.AdjustCompensation(new AdjustCompensationViewModel
        {
            IncidentId = incidentId,
            NewAmount = 180000m,
            Reason = "Điều chỉnh qua Controller MVC"
        });

        var redirectAdjust = Assert.IsType<RedirectToActionResult>(adjustResult);
        Assert.Equal("Index", redirectAdjust.ActionName);

        var comp = await _db.BookIncidentCompensations
            .FirstAsync(c => c.BookIncidentId == incidentId);
        Assert.Equal(180000m, comp.CurrentAmount);
        Assert.True(comp.IsAdjusted);
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
