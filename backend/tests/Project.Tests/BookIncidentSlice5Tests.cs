using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Project.Controllers;
using Project.Data;
using Project.Models;
using Project.Services;
using Xunit;

namespace Project.Tests;

public class BookIncidentSlice5Tests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly BookIncidentService _incidentService;
    private readonly CompensationService _compensationService;

    public BookIncidentSlice5Tests()
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
            DueDate = dDate,
            IsReturned = false,
            OverdueFee = initialOverdueFee,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.BookLoans.Add(loan);
        await _db.SaveChangesAsync();

        return (book, copy, reader, loan);
    }

    private async Task<RecordIncidentOutcome> RecordTestIncidentAsync(
        long loanId,
        long copyId,
        string incidentType = BookIncidentType.Lost,
        string description = "Bạn đọc báo làm rơi mất sách",
        string staffEmail = "thuthu@test.local")
    {
        return await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loanId,
            BookCopyId = copyId,
            IncidentType = incidentType,
            Description = description,
            StaffEmail = staffEmail,
            IpAddress = "127.0.0.1"
        });
    }

    // -------------------------------------------------------------------------
    // TC 01: Lập bút toán điều chỉnh thành công khi sự cố đủ điều kiện, có lý do, có căn cứ
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC01_CreateAdjustmentVoucher_Success_WhenEligible_WithReasonAndReference()
    {
        await SeedCompensationPolicyAsync(lostRate: 1.0m);
        var (_, copy, _, loan) = await SeedLoanAsync(coverPrice: 150_000m);
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id, BookIncidentType.Lost);
        Assert.True(recordOutcome.IsSuccess);

        var request = new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Thủ thư ghi nhận nhầm mã vạch của bản sao sách",
            ReferenceDocument = "Biên bản kiểm kê số 12/BB-TV",
            CopyAction = CopyReversalActions.RestoreToLoan,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local",
            Notes = "Cần mở lại phiếu mượn cho bạn đọc"
        };

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(request);

        Assert.True(outcome.IsSuccess, outcome.ErrorMessage);
        Assert.NotNull(outcome.Voucher);
        Assert.StartsWith("ADJ-", outcome.Voucher.VoucherCode);
        Assert.Equal(recordOutcome.Incident.Id, outcome.Voucher.BookIncidentId);
        Assert.Equal(loan.Id, outcome.Voucher.BookLoanId);
        Assert.Equal(copy.Id, outcome.Voucher.BookCopyId);
        Assert.Equal("Thủ thư ghi nhận nhầm mã vạch của bản sao sách", outcome.Voucher.Reason);
        Assert.Equal("Biên bản kiểm kê số 12/BB-TV", outcome.Voucher.ReferenceDocument);
        Assert.Equal("quanly@test.local", outcome.Voucher.CreatedBy);
        Assert.Equal("quanly@test.local", outcome.Voucher.ApprovedBy);
        Assert.Equal("Đã xác nhận", outcome.Voucher.Status);
    }

    // -------------------------------------------------------------------------
    // TC 02: Từ chối lập bút toán khi thiếu lý do (Reason rỗng hoặc quá ngắn)
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234")] // Dưới 5 ký tự
    public async Task TC02_CreateAdjustmentVoucher_Rejects_WhenReasonMissingOrTooShort(string? reason)
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var request = new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = reason!,
            ReferenceDocument = "Biên bản kiểm kê số 01",
            CopyAction = CopyReversalActions.RestoreToLoan,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        };

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("lý do điều chỉnh", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // TC 03: Từ chối lập bút toán khi thiếu tài liệu/biên bản căn cứ (ReferenceDocument rỗng hoặc quá ngắn)
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")] // Dưới 2 ký tự
    public async Task TC03_CreateAdjustmentVoucher_Rejects_WhenReferenceDocumentMissingOrTooShort(string? referenceDoc)
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var request = new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Ghi nhận nhầm sách cần hoàn tác",
            ReferenceDocument = referenceDoc!,
            CopyAction = CopyReversalActions.RestoreToLoan,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        };

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("căn cứ", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // TC 04: Từ chối vai trò không được phép (ví dụ Librarian hoặc role không có quyền quản lý)
    // -------------------------------------------------------------------------
    [Theory]
    [InlineData(AccountRoles.Librarian)]
    [InlineData("Staff")]
    [InlineData("Reader")]
    [InlineData("")]
    public async Task TC04_CreateAdjustmentVoucher_Rejects_UnauthorizedRole(string role)
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var request = new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Nhập nhầm mã sự cố",
            ReferenceDocument = "Biên bản kiểm kê số 05",
            CopyAction = CopyReversalActions.RestoreToLoan,
            StaffRole = role,
            StaffEmail = "staff@test.local"
        };

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("Quản lý thư viện hoặc Quản trị hệ thống", outcome.ErrorMessage);
    }

    // -------------------------------------------------------------------------
    // TC 05: Từ chối sự cố không tồn tại hoặc không hợp lệ
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC05_CreateAdjustmentVoucher_Rejects_NonExistentIncident()
    {
        var request = new CreateAdjustmentVoucherRequest
        {
            IncidentId = 999999,
            Reason = "Sự cố không tồn tại trong hệ thống",
            ReferenceDocument = "Biên bản 999",
            StaffRole = AccountRoles.SystemAdmin,
            StaffEmail = "admin@test.local"
        };

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("Không tìm thấy", outcome.ErrorMessage);
    }

    // -------------------------------------------------------------------------
    // TC 06: Kiểm tra bảo toàn nhật ký gốc Lát 4: nhật ký sự cố gốc không bị xóa/sửa
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC06_OriginalAuditLog_IsPreserved_NotModifiedOrDeleted()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var originalLogs = await _db.BookIncidentAuditLogs
            .Where(l => l.BookLoanId == loan.Id)
            .OrderBy(l => l.Id)
            .ToListAsync();
        Assert.NotEmpty(originalLogs);
        var originalLogIds = originalLogs.Select(l => l.Id).ToList();
        var originalLogSummaries = originalLogs.Select(l => l.Summary).ToList();

        // Tiến hành lập bút toán điều chỉnh
        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Đảo sự cố do ghi nhận nhầm",
            ReferenceDocument = "Quyết định xử lý sự cố số 01/QĐ",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });
        Assert.True(outcome.IsSuccess);

        // Kiểm tra toàn bộ nhật ký gốc vẫn tồn tại nguyên vẹn
        var logsAfter = await _db.BookIncidentAuditLogs
            .Where(l => l.BookLoanId == loan.Id)
            .OrderBy(l => l.Id)
            .ToListAsync();

        Assert.True(logsAfter.Count > originalLogs.Count); // Thêm mới log bút toán, không mất log cũ
        foreach (var originalId in originalLogIds)
        {
            var log = logsAfter.FirstOrDefault(l => l.Id == originalId);
            Assert.NotNull(log);
            Assert.Contains(log.Summary, originalLogSummaries);
        }
    }

    // -------------------------------------------------------------------------
    // TC 07: Kiểm tra liên kết dữ liệu bút toán với sự cố gốc
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC07_AdjustmentVoucher_LinkedToOriginalIncident()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Đảo nghiệp vụ do nhầm lẫn",
            ReferenceDocument = "Biên bản kiểm kê số 88",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });

        Assert.True(outcome.IsSuccess);
        var voucher = outcome.Voucher!;
        Assert.Equal(recordOutcome.Incident!.Id, voucher.BookIncidentId);
        Assert.Equal(loan.Id, voucher.BookLoanId);
        Assert.Equal(copy.Id, voucher.BookCopyId);

        var refreshedIncident = await _db.BookIncidents.FindAsync(recordOutcome.Incident.Id);
        Assert.NotNull(refreshedIncident);
        Assert.True(refreshedIncident.IsReversed);
        Assert.Equal(voucher.Id, refreshedIncident.ReversalVoucherId);
    }

    // -------------------------------------------------------------------------
    // TC 08: Kiểm tra trạng thái bản sao sau khi đảo:
    //   - CopyAction = RestoreToLoan -> OnLoan
    //   - CopyAction = ReturnToInventory -> Available
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC08_CopyStatus_RestoredAppropriately_BasedOnCopyAction()
    {
        await SeedCompensationPolicyAsync();

        // 8a. RestoreToLoan
        var (_, copy1, _, loan1) = await SeedLoanAsync();
        var record1 = await RecordTestIncidentAsync(loan1.Id, copy1.Id);
        var outcome1 = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = record1.Incident!.Id,
            Reason = "Nhầm lẫn, bạn đọc vẫn đang giữ sách mượn",
            ReferenceDocument = "Biên bản số 01",
            CopyAction = CopyReversalActions.RestoreToLoan,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });
        Assert.True(outcome1.IsSuccess);
        var refreshedCopy1 = await _db.BookCopies.FindAsync(copy1.Id);
        Assert.Equal(BookCopyStatus.OnLoan, refreshedCopy1!.Status);

        // 8b. ReturnToInventory
        var (_, copy2, _, loan2) = await SeedLoanAsync();
        var record2 = await RecordTestIncidentAsync(loan2.Id, copy2.Id);
        var outcome2 = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = record2.Incident!.Id,
            Reason = "Tìm lại được sách, nhập lại kho thư viện",
            ReferenceDocument = "Biên bản hoàn trả số 02",
            CopyAction = CopyReversalActions.ReturnToInventory,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });
        Assert.True(outcome2.IsSuccess);
        var refreshedCopy2 = await _db.BookCopies.FindAsync(copy2.Id);
        Assert.Equal(BookCopyStatus.Available, refreshedCopy2!.Status);
    }

    // -------------------------------------------------------------------------
    // TC 09: Kiểm tra số lượng bản sao rảnh sau khi đảo (tăng khi ReturnToInventory)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC09_AvailableCopiesCount_Increases_WhenReturnedToInventory()
    {
        await SeedCompensationPolicyAsync();
        var (book, copy1, _, loan1) = await SeedLoanAsync();

        // Thêm 1 bản sao rảnh sẵn có
        var copyAvailable = new BookCopy
        {
            BookId = book.Id,
            CopyCode = "BC-AVAIL-" + Guid.NewGuid().ToString("N")[..4],
            Status = BookCopyStatus.Available,
            PhysicalCondition = BookCopyCondition.Good
        };
        _db.BookCopies.Add(copyAvailable);
        await _db.SaveChangesAsync();

        var countBeforeIncident = await _db.BookCopies.CountAsync(c => c.BookId == book.Id && c.Status == BookCopyStatus.Available);
        Assert.Equal(1, countBeforeIncident);

        // Ghi nhận sự cố -> copy1 chuyển Removed
        var recordOutcome = await RecordTestIncidentAsync(loan1.Id, copy1.Id);
        Assert.True(recordOutcome.IsSuccess);

        var countDuringIncident = await _db.BookCopies.CountAsync(c => c.BookId == book.Id && c.Status == BookCopyStatus.Available);
        Assert.Equal(1, countDuringIncident);

        // Đảo nghiệp vụ với ReturnToInventory -> copy1 chuyển Available
        var reversalOutcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Bạn đọc đem trả lại sách nguyên vẹn",
            ReferenceDocument = "Biên bản tiếp nhận sách trả số 10",
            CopyAction = CopyReversalActions.ReturnToInventory,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });
        Assert.True(reversalOutcome.IsSuccess);

        var countAfterReversal = await _db.BookCopies.CountAsync(c => c.BookId == book.Id && c.Status == BookCopyStatus.Available);
        Assert.Equal(2, countAfterReversal); // Tăng thêm 1 bản sao rảnh
    }

    // -------------------------------------------------------------------------
    // TC 10: Kiểm tra trạng thái phiếu mượn sau khi đảo:
    //   - RestoreToLoan: Phiếu mở lại (IsReturned = false, CloseReason = null, ReturnedAtUtc = null)
    //   - ReturnToInventory: Phiếu đóng hoàn tất (IsReturned = true, CloseReason = "Đã trả")
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC10_BookLoanStatus_UpdatedAppropriately_BasedOnCopyAction()
    {
        await SeedCompensationPolicyAsync();

        // 10a. RestoreToLoan
        var (_, copy1, _, loan1) = await SeedLoanAsync();
        var record1 = await RecordTestIncidentAsync(loan1.Id, copy1.Id);
        var outcome1 = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = record1.Incident!.Id,
            Reason = "Ghi nhận nhầm, mở lại phiếu mượn",
            ReferenceDocument = "Căn cứ 01",
            CopyAction = CopyReversalActions.RestoreToLoan,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });
        Assert.True(outcome1.IsSuccess);
        var refreshedLoan1 = await _db.BookLoans.FindAsync(loan1.Id);
        Assert.False(refreshedLoan1!.IsReturned);
        Assert.Null(refreshedLoan1.CloseReason);
        Assert.Null(refreshedLoan1.ReturnedAtUtc);

        // 10b. ReturnToInventory
        var (_, copy2, _, loan2) = await SeedLoanAsync();
        var record2 = await RecordTestIncidentAsync(loan2.Id, copy2.Id);
        var outcome2 = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = record2.Incident!.Id,
            Reason = "Sách đã được hoàn trả, đóng phiếu mượn hợp lệ",
            ReferenceDocument = "Căn cứ 02",
            CopyAction = CopyReversalActions.ReturnToInventory,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });
        Assert.True(outcome2.IsSuccess);
        var refreshedLoan2 = await _db.BookLoans.FindAsync(loan2.Id);
        Assert.True(refreshedLoan2!.IsReturned);
        Assert.Equal(BookLoanCloseReason.Returned, refreshedLoan2.CloseReason);
        Assert.NotNull(refreshedLoan2.ReturnedAtUtc);
    }

    // -------------------------------------------------------------------------
    // TC 11: Kiểm tra tiền bồi thường sau khi đảo (về 0đ, lưu lịch sử điều chỉnh bồi thường)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC11_Compensation_ResetToZero_WithAdjustmentHistory()
    {
        await SeedCompensationPolicyAsync(lostRate: 1.0m);
        var (_, copy, _, loan) = await SeedLoanAsync(coverPrice: 200_000m);
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);
        Assert.NotNull(recordOutcome.Compensation);
        Assert.Equal(200_000m, recordOutcome.Compensation.CurrentAmount);

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Hủy sự cố do nhầm mã sách",
            ReferenceDocument = "Biên bản kiểm kê số 33",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });

        Assert.True(outcome.IsSuccess);

        var refreshedComp = await _db.BookIncidentCompensations
            .Include(c => c.AdjustmentHistories)
            .FirstOrDefaultAsync(c => c.BookIncidentId == recordOutcome.Incident.Id);

        Assert.NotNull(refreshedComp);
        Assert.Equal(0m, refreshedComp.CurrentAmount);
        Assert.True(refreshedComp.IsAdjusted);
        Assert.Contains("quanly@test.local", refreshedComp.AdjustedBy);

        var latestHistory = refreshedComp.AdjustmentHistories.OrderByDescending(h => h.Id).FirstOrDefault();
        Assert.NotNull(latestHistory);
        Assert.Equal(200_000m, latestHistory.FromAmount);
        Assert.Equal(0m, latestHistory.ToAmount);
        Assert.Contains(outcome.Voucher!.VoucherCode, latestHistory.Reason);
    }

    // -------------------------------------------------------------------------
    // TC 12: Kiểm tra trường hợp bồi thường đã thanh toán (ghi chú đối chiếu hoàn tiền)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC12_PaidCompensation_RetainsReferenceAndNotes_ForReconciliation()
    {
        await SeedCompensationPolicyAsync(lostRate: 1.0m);
        var (_, copy, _, loan) = await SeedLoanAsync(coverPrice: 150_000m);
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var request = new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Bạn đọc đã tìm thấy sách sau khi đã nộp phạt bồi thường",
            ReferenceDocument = "Biên bản nộp phạt số 99 & Đơn xin hoàn tiền",
            CopyAction = CopyReversalActions.ReturnToInventory,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local",
            Notes = "Kế toán thực hiện thủ tục hoàn trả 150.000 VNĐ cho bạn đọc theo phiếu chi số 44"
        };

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(150_000m, outcome.Voucher!.PreviousCompensationAmount);
        Assert.Equal(0m, outcome.Voucher.NewCompensationAmount);
        Assert.Contains("hoàn trả 150.000 VNĐ", outcome.Voucher.Notes);
    }

    // -------------------------------------------------------------------------
    // TC 13: Kiểm tra bảo toàn phí trễ hạn (phí trễ hạn phát sinh giữ nguyên sau khi đảo)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC13_OverdueFee_IsPreserved_UnchangedAfterReversal()
    {
        await SeedCompensationPolicyAsync();
        // Quá hạn 5 ngày: 5 * 2000 = 10.000đ
        var pastDue = DateOnly.FromDateTime(DateTime.Today.AddDays(-5));
        var (_, copy, _, loan) = await SeedLoanAsync(dueDate: pastDue);

        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);
        Assert.True(recordOutcome.IsSuccess);
        Assert.Equal(10_000m, recordOutcome.OverdueFee);

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Hủy sự cố nhưng bảo toàn phí quá hạn của bạn đọc",
            ReferenceDocument = "Biên bản ghi nhận ngày trả",
            CopyAction = CopyReversalActions.ReturnToInventory,
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });

        Assert.True(outcome.IsSuccess);
        var refreshedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.Equal(10_000m, refreshedLoan!.OverdueFee);
        Assert.Equal(10_000m, outcome.Voucher!.PreviousOverdueFee);
        Assert.Equal(10_000m, outcome.Voucher.NewOverdueFee);
    }

    // -------------------------------------------------------------------------
    // TC 14: Kiểm tra hiển thị lịch sử nghiệp vụ đầy đủ: người lập, ngày, lý do, căn cứ
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC14_HistoryViewModel_DisplaysCompleteAuditTrailAndVouchers()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var reversalOutcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Sửa sai thông tin ghi nhận",
            ReferenceDocument = "Biên bản đối soát số 77/BB-DS",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });
        Assert.True(reversalOutcome.IsSuccess);

        var historyModel = await _incidentService.GetLoanIncidentHistoryAsync(loan.Id);

        Assert.NotNull(historyModel);
        Assert.NotEmpty(historyModel.Vouchers);
        var voucher = historyModel.Vouchers[0];
        Assert.Equal("Sửa sai thông tin ghi nhận", voucher.Reason);
        Assert.Equal("Biên bản đối soát số 77/BB-DS", voucher.ReferenceDocument);
        Assert.Equal("quanly@test.local", voucher.CreatedBy);
        Assert.Equal("Đã xác nhận", voucher.Status);

        // Nhật ký kiểm toán có dòng Bút toán điều chỉnh
        var adjustmentLog = historyModel.Logs.FirstOrDefault(l => l.OperationType == "Bút toán điều chỉnh");
        Assert.NotNull(adjustmentLog);
        Assert.Contains("quanly@test.local", adjustmentLog.Actor);
        Assert.Contains("Biên bản đối soát số 77/BB-DS", adjustmentLog.Summary);
    }

    // -------------------------------------------------------------------------
    // TC 15: Ngăn chặn đảo lặp lại (sự cố đã đảo không thể đảo lần nữa)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC15_PreventDuplicateReversal_CannotReverseTwice()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var firstRequest = new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Đảo sự cố lần đầu",
            ReferenceDocument = "Căn cứ 01",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        };
        var firstOutcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(firstRequest);
        Assert.True(firstOutcome.IsSuccess);

        // Đảo lần 2 cho cùng 1 sự cố
        var secondRequest = new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Cố tình đảo tiếp lần 2",
            ReferenceDocument = "Căn cứ 02",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        };
        var secondOutcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(secondRequest);

        Assert.False(secondOutcome.IsSuccess);
        Assert.Contains("đã được lập bút toán đảo trước đó", secondOutcome.ErrorMessage);
    }

    // -------------------------------------------------------------------------
    // TC 16: Kiểm tra xử lý đồng thời (Concurrency check - không tạo trùng voucher)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC16_ConcurrentAdjustment_Prevented_OrConsistencyMaintained()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var request1 = new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Đảo sự cố qua luồng 1",
            ReferenceDocument = "Căn cứ đồng thời 1",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly1@test.local"
        };
        var request2 = new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Đảo sự cố qua luồng 2",
            ReferenceDocument = "Căn cứ đồng thời 2",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly2@test.local"
        };

        var outcome1 = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(request1);
        var outcome2 = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(request2);

        // Chỉ đúng 1 yêu cầu thành công, yêu cầu còn lại bị chặn
        Assert.True(outcome1.IsSuccess ^ outcome2.IsSuccess);
        var vouchersCount = await _db.IncidentAdjustmentVouchers.CountAsync(v => v.BookIncidentId == recordOutcome.Incident.Id);
        Assert.Equal(1, vouchersCount);
    }

    // -------------------------------------------------------------------------
    // TC 17: Kiểm tra tính nguyên tử (Rollback khi có lỗi, không lưu dữ liệu dở dang)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC17_Rollback_OnFailure_LeavesNoDanglingState()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();

        // Tạo sự cố có tham chiếu bản sao không tồn tại trong hệ thống để kích hoạt nhánh lỗi
        var orphanIncident = new BookIncident
        {
            BookLoanId = loan.Id,
            BookCopyId = 999999, // Không tồn tại
            IncidentType = BookIncidentType.Lost,
            Description = "Sự cố thiếu bản sao",
            ReportedBy = "tester",
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.BookIncidents.Add(orphanIncident);
        await _db.SaveChangesAsync();

        var request = new CreateAdjustmentVoucherRequest
        {
            IncidentId = orphanIncident.Id,
            Reason = "Thử nghiệm lỗi rollback khi thiếu bản sao",
            ReferenceDocument = "Biên bản 99",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        };

        var outcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("Không tìm thấy", outcome.ErrorMessage);

        // Kiểm tra tính nguyên tử: không tạo voucher nào, incident không bị đánh dấu IsReversed
        var vouchersCount = await _db.IncidentAdjustmentVouchers.CountAsync();
        Assert.Equal(0, vouchersCount);

        var refreshedIncident = await _db.BookIncidents.FindAsync(orphanIncident.Id);
        Assert.NotNull(refreshedIncident);
        Assert.False(refreshedIncident.IsReversed);
    }

    // -------------------------------------------------------------------------
    // TC 18: Kiểm tra bảo vệ bất biến cấp DbContext: Thử sửa hoặc xóa voucher/audit log sẽ quăng InvalidOperationException
    // -------------------------------------------------------------------------
    [Fact]
    public async Task TC18_Immutability_DbContext_ThrowsOnUpdateOrDelete_ForVoucherAndAuditLog()
    {
        await SeedCompensationPolicyAsync();
        var (_, copy, _, loan) = await SeedLoanAsync();
        var recordOutcome = await RecordTestIncidentAsync(loan.Id, copy.Id);

        var voucherOutcome = await _incidentService.CreateAndApplyAdjustmentVoucherAsync(new CreateAdjustmentVoucherRequest
        {
            IncidentId = recordOutcome.Incident!.Id,
            Reason = "Lập bút toán để test tính bất biến",
            ReferenceDocument = "BB số 100",
            StaffRole = AccountRoles.LibraryManager,
            StaffEmail = "quanly@test.local"
        });
        Assert.True(voucherOutcome.IsSuccess);

        var voucher = await _db.IncidentAdjustmentVouchers.FirstAsync(v => v.Id == voucherOutcome.Voucher!.Id);
        var auditLog = await _db.BookIncidentAuditLogs.FirstAsync(l => l.BookLoanId == loan.Id);

        // 18a. Thử sửa voucher -> Phải quăng InvalidOperationException
        voucher.Reason = "Cố tình sửa lý do voucher";
        var exUpdateVoucher = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        Assert.Contains("không được sửa hoặc xóa", exUpdateVoucher.Message);

        _db.Entry(voucher).State = EntityState.Unchanged;

        // 18b. Thử xóa voucher -> Phải quăng InvalidOperationException
        _db.IncidentAdjustmentVouchers.Remove(voucher);
        var exDeleteVoucher = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        Assert.Contains("không được sửa hoặc xóa", exDeleteVoucher.Message);

        _db.Entry(voucher).State = EntityState.Unchanged;

        // 18c. Thử sửa audit log -> Phải quăng InvalidOperationException
        auditLog.Summary = "Cố tình sửa tóm tắt audit log";
        var exUpdateLog = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        Assert.Contains("không được sửa hoặc xóa", exUpdateLog.Message);

        _db.Entry(auditLog).State = EntityState.Unchanged;

        // 18d. Thử xóa audit log -> Phải quăng InvalidOperationException
        _db.BookIncidentAuditLogs.Remove(auditLog);
        var exDeleteLog = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        Assert.Contains("không được sửa hoặc xóa", exDeleteLog.Message);
    }

    // -------------------------------------------------------------------------
    // TC 19: Kiểm tra bảo vệ giao diện/API: Không có phương thức hoặc API Update/Delete voucher/audit log
    // -------------------------------------------------------------------------
    [Fact]
    public void TC19_ControllerAndService_ExposeNoUpdateOrDeleteApi_ForVoucherOrAuditLog()
    {
        var controllerType = typeof(BookIncidentController);
        var methods = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            var name = method.Name.ToLowerInvariant();
            Assert.False(name.Contains("updatevoucher") || name.Contains("deletevoucher")
                || name.Contains("updateauditlog") || name.Contains("deleteauditlog"),
                $"Controller không được phép chứa phương thức sửa/xóa bút toán hoặc nhật ký: {method.Name}");

            var httpDelete = method.GetCustomAttribute<HttpDeleteAttribute>();
            var httpPut = method.GetCustomAttribute<HttpPutAttribute>();
            var httpPatch = method.GetCustomAttribute<HttpPatchAttribute>();

            Assert.Null(httpDelete);
            Assert.Null(httpPut);
            Assert.Null(httpPatch);
        }

        var serviceType = typeof(BookIncidentService);
        var serviceMethods = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var method in serviceMethods)
        {
            var name = method.Name.ToLowerInvariant();
            Assert.False(name.Contains("deletevoucher") || name.Contains("updatevoucher")
                || name.Contains("deleteauditlog") || name.Contains("updateauditlog"),
                $"Service không được phép chứa phương thức sửa/xóa bút toán hoặc nhật ký: {method.Name}");
        }
    }
}

