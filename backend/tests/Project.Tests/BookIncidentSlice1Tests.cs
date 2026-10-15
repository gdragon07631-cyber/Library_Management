using System.Text;
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

public class BookIncidentSlice1Tests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly BookIncidentService _incidentService;
    private readonly string _testUploadsDir;

    public BookIncidentSlice1Tests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDbContext(options);
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
        string copyStatus = BookCopyStatus.OnLoan,
        string physicalCondition = BookCopyCondition.Good)
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

        var warehouse = new Warehouse { Code = "KHO-" + Guid.NewGuid().ToString("N")[..4], Name = "Kho trung tâm" };
        _db.Warehouses.Add(warehouse);

        var shelf = new Shelf { Code = "KE-" + Guid.NewGuid().ToString("N")[..4], Name = "Kệ A1", Warehouse = warehouse };
        _db.Shelves.Add(shelf);

        var copy = new BookCopy
        {
            Book = book,
            Shelf = shelf,
            CopyCode = "CP-" + Guid.NewGuid().ToString("N")[..6],
            Status = copyStatus,
            PhysicalCondition = physicalCondition
        };
        _db.BookCopies.Add(copy);

        var cardType = new LibraryCardType { Name = "Thẻ chuẩn", MaxBooks = 5 };
        _db.LibraryCardTypes.Add(cardType);

        var reader = new ReaderAccount
        {
            FullName = "Nguyễn Văn Bạn Đọc",
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
            Book = book,
            BookCopy = copy,
            BookCopyId = copy.Id,
            ReaderAccount = reader,
            ReaderAccountId = reader.Id,
            LoanDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-5)),
            OriginalDueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(9)),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(9)),
            ReturnedAtUtc = null
        };
        _db.BookLoans.Add(loan);

        await _db.SaveChangesAsync();
        return (book, copy, reader, loan);
    }

    private static IFormFile CreateMockFormFile(string fileName, string contentType, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "photo", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    // 1. Ghi nhận bản sao bị Mất thành công
    [Fact]
    public async Task Test01_RecordLostCopy_Success_UpdatesStatusToRemoved_AndKeepsLoanOpen()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Bạn đọc báo bị mất sách trên xe buýt",
            StaffEmail = "librarian@library.edu.vn"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.NotNull(outcome.Incident);
        Assert.Equal(BookIncidentType.Lost, outcome.Incident.IncidentType);
        Assert.Equal("Bạn đọc báo bị mất sách trên xe buýt", outcome.Incident.Description);

        // Bản sao chuyển sang trạng thái "Đã loại khỏi kho"
        var updatedCopy = await _db.BookCopies.FindAsync(copy.Id);
        Assert.NotNull(updatedCopy);
        Assert.Equal(BookCopyStatus.Removed, updatedCopy.Status);
        Assert.Contains(BookIncidentType.Lost, updatedCopy.StatusReason);

        // Lịch sử trạng thái được ghi nhận
        var history = await _db.BookCopyStatusHistories.FirstOrDefaultAsync(h => h.BookCopyId == copy.Id);
        Assert.NotNull(history);
        Assert.Equal(BookCopyStatus.OnLoan, history.FromStatus);
        Assert.Equal(BookCopyStatus.Removed, history.ToStatus);
        Assert.Contains("Bạn đọc báo bị mất sách", history.Reason);

        // Phiếu mượn được đóng với lý do Mất (Lát 3)
        var updatedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(updatedLoan);
        Assert.NotNull(updatedLoan.ReturnedAtUtc);
        Assert.True(updatedLoan.IsReturned);
        Assert.Equal(BookIncidentType.Lost, updatedLoan.CloseReason);
    }

    // 2. Ghi nhận bản sao bị Hỏng nặng thành công
    [Fact]
    public async Task Test02_RecordDamagedCopy_Success_UpdatesStatusToRemoved_AndPhysicalConditionToMajorDamage()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync(physicalCondition: BookCopyCondition.Good);

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Bản sao bị đổ cà phê ướt toàn bộ ruột sách, mục nát rách rời",
            StaffEmail = "librarian@library.edu.vn"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.True(outcome.IsSuccess);

        // Bản sao chuyển sang trạng thái "Đã loại khỏi kho" và tình trạng "Hư hỏng nặng"
        var updatedCopy = await _db.BookCopies.FindAsync(copy.Id);
        Assert.NotNull(updatedCopy);
        Assert.Equal(BookCopyStatus.Removed, updatedCopy.Status);
        Assert.Equal(BookCopyCondition.MajorDamage, updatedCopy.PhysicalCondition);
        Assert.Contains(BookIncidentType.Damaged, updatedCopy.StatusReason);

        // Phiếu mượn được đóng với lý do Hỏng nặng (Lát 3)
        var updatedLoan = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(updatedLoan);
        Assert.NotNull(updatedLoan.ReturnedAtUtc);
        Assert.True(updatedLoan.IsReturned);
        Assert.Equal(BookIncidentType.Damaged, updatedLoan.CloseReason);
    }

    // 3. Lưu đúng mô tả tình trạng
    [Fact]
    public async Task Test03_RecordIncident_SavesExactDescription_InIncidentAndHistory()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();
        const string exactDesc = "Mất sách khi làm thí nghiệm tại phòng lab A2, đã kiểm tra kỹ không tìm thấy.";

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = exactDesc,
            StaffEmail = "staff@library.com"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.True(outcome.IsSuccess);

        var incident = await _db.BookIncidents.FirstAsync(i => i.BookCopyId == copy.Id);
        Assert.Equal(exactDesc, incident.Description);

        var copyInDb = await _db.BookCopies.FindAsync(copy.Id);
        Assert.Contains(exactDesc, copyInDb!.StatusReason);

        var history = await _db.BookCopyStatusHistories.FirstAsync(h => h.BookCopyId == copy.Id);
        Assert.Contains(exactDesc, history.Reason);
    }

    // 4. Lưu ảnh chụp khi có ảnh
    [Fact]
    public async Task Test04_RecordIncident_WithPhoto_SavesPhotoPath()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();
        var fakeImageBytes = Encoding.UTF8.GetBytes("fake-image-content-for-testing");
        var photoFile = CreateMockFormFile("damaged.jpg", "image/jpeg", fakeImageBytes);

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Sách bị cháy góc",
            Photo = photoFile,
            StaffEmail = "staff@library.com"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.NotNull(outcome.Incident?.PhotoPath);
        Assert.StartsWith("/uploads/incidents/", outcome.Incident!.PhotoPath);
    }

    // 5. Cho phép ghi nhận khi không đính kèm ảnh
    [Fact]
    public async Task Test05_RecordIncident_WithoutPhoto_SucceedsWithNullPhotoPath()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Không tìm thấy sách",
            Photo = null,
            StaffEmail = "staff@library.com"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.Null(outcome.Incident?.PhotoPath);

        var copyInDb = await _db.BookCopies.FindAsync(copy.Id);
        Assert.Equal(BookCopyStatus.Removed, copyInDb!.Status);
    }

    // 6. Từ chối dữ liệu thiếu thông tin bắt buộc
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Test06_RecordIncident_MissingDescription_ReturnsFailure(string? emptyDescription)
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = emptyDescription!
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("mô tả", outcome.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Hỏng nhẹ")]
    [InlineData("Mất một phần")]
    [InlineData("Bình thường")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Test06_RecordIncident_InvalidIncidentType_ReturnsFailure(string? invalidType)
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = invalidType!,
            Description = "Có mô tả hợp lệ"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("tình trạng sự cố", outcome.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test06_RecordIncident_InvalidLoanOrCopyId_ReturnsFailure()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();

        var req1 = new RecordIncidentRequest
        {
            LoanId = 0,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mô tả hợp lệ"
        };
        var outcome1 = await _incidentService.RecordIncidentAsync(req1);
        Assert.False(outcome1.IsSuccess);

        var req2 = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = 0,
            IncidentType = BookIncidentType.Lost,
            Description = "Mô tả hợp lệ"
        };
        var outcome2 = await _incidentService.RecordIncidentAsync(req2);
        Assert.False(outcome2.IsSuccess);
    }

    // 7. Từ chối phiếu mượn không tồn tại hoặc đã đóng
    [Fact]
    public async Task Test07_RecordIncident_NonExistentLoan_ReturnsFailure()
    {
        var (_, copy, _, _) = await SeedOpenLoanAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = 999999, // Không tồn tại
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mô tả"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("không tồn tại", outcome.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test07_RecordIncident_ClosedLoan_ReturnsFailure()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();
        loan.ReturnedAtUtc = DateTime.UtcNow.AddDays(-1);
        await _db.SaveChangesAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mô tả hợp lệ"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("đã đóng hoặc đã được trả", outcome.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // 8. Từ chối bản sao không thuộc phiếu mượn được chọn
    [Fact]
    public async Task Test08_RecordIncident_CopyNotBelongingToLoan_ReturnsFailure()
    {
        var (book, copy, _, loan) = await SeedOpenLoanAsync();

        // Tạo một bản sao khác không gắn vào phiếu mượn này
        var otherCopy = new BookCopy
        {
            BookId = book.Id,
            ShelfId = copy.ShelfId,
            CopyCode = "OTHER-COPY-" + Guid.NewGuid().ToString("N")[..4],
            Status = BookCopyStatus.OnLoan
        };
        _db.BookCopies.Add(otherCopy);
        await _db.SaveChangesAsync();

        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = otherCopy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Cố tình ghi nhận nhầm bản sao"
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("không thuộc phiếu mượn", outcome.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // Bản sao khác không bị thay đổi trạng thái
        var copyInDb = await _db.BookCopies.FindAsync(otherCopy.Id);
        Assert.Equal(BookCopyStatus.OnLoan, copyInDb!.Status);
    }

    // 9. Không cho ghi nhận lặp đối với bản sao đã bị loại khỏi kho
    [Fact]
    public async Task Test09_RecordIncident_AlreadyRemovedCopy_OrDuplicateIncident_ReturnsFailure()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();

        // Lần 1: Ghi nhận thành công
        var request1 = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Lần đầu ghi nhận mất sách"
        };
        var outcome1 = await _incidentService.RecordIncidentAsync(request1);
        Assert.True(outcome1.IsSuccess);

        // Lần 2: Cố tình ghi nhận lại
        var request2 = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Ghi nhận lặp lại lần 2"
        };
        var outcome2 = await _incidentService.RecordIncidentAsync(request2);

        Assert.False(outcome2.IsSuccess);
        Assert.Contains("đã bị loại khỏi kho", outcome2.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // 10. Xác nhận bản sao chuyển sang trạng thái Đã loại khỏi kho sau khi ghi nhận thành công
    [Fact]
    public async Task Test10_RecordIncident_CopyStatusBecomesRemoved()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();

        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Mô tả hư hỏng nặng"
        });

        Assert.True(outcome.IsSuccess);
        var copyFromDb = await _db.BookCopies.FindAsync(copy.Id);
        Assert.Equal(BookCopyStatus.Removed, copyFromDb!.Status);
        Assert.Equal(BookCopyCondition.MajorDamage, copyFromDb.PhysicalCondition);
    }

    // 11. Xác nhận số bản sách rảnh được tính chính xác sau khi loại bản sao
    [Fact]
    public async Task Test11_RecordIncident_RemovedCopyNotCountedInAvailableCopies()
    {
        var (book, copyOnLoan, _, loan) = await SeedOpenLoanAsync();

        // Thêm một bản sao Sẵn sàng (Available) cho cùng đầu sách
        var availableCopy = new BookCopy
        {
            BookId = book.Id,
            ShelfId = copyOnLoan.ShelfId,
            CopyCode = "AVAIL-" + Guid.NewGuid().ToString("N")[..4],
            Status = BookCopyStatus.Available
        };
        _db.BookCopies.Add(availableCopy);
        await _db.SaveChangesAsync();

        // Trước khi ghi nhận sự cố: số bản rảnh là 1 (chính là availableCopy)
        var countBefore = await _db.BookCopies.CountAsync(c => c.BookId == book.Id && c.Status == BookCopyStatus.Available);
        Assert.Equal(1, countBefore);

        // Ghi nhận sự cố bản sao đang mượn
        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copyOnLoan.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Mất sách"
        });
        Assert.True(outcome.IsSuccess);

        // Sau khi loại khỏi kho: bản sao bị mất có Status == "Đã loại khỏi kho", KHÔNG được tính vào số bản rảnh
        var countAfter = await _db.BookCopies.CountAsync(c => c.BookId == book.Id && c.Status == BookCopyStatus.Available);
        Assert.Equal(1, countAfter);

        // Đảm bảo bản sao bị loại không thuộc nhóm rảnh
        var removedCopy = await _db.BookCopies.FindAsync(copyOnLoan.Id);
        Assert.Equal(BookCopyStatus.Removed, removedCopy!.Status);
        Assert.NotEqual(BookCopyStatus.Available, removedCopy.Status);
    }

    // 12. Xác nhận thao tác thất bại không làm thay đổi dữ liệu
    [Fact]
    public async Task Test12_RecordIncident_FailedOperation_DoesNotMutateData()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();
        var originalStatus = copy.Status;
        var originalCondition = copy.PhysicalCondition;

        // Thao tác với dữ liệu không hợp lệ (mô tả rỗng)
        var request = new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "" // Thiếu mô tả
        };

        var outcome = await _incidentService.RecordIncidentAsync(request);

        Assert.False(outcome.IsSuccess);

        // Kiểm tra dữ liệu bản sao không bị thay đổi dở dang
        var copyInDb = await _db.BookCopies.FindAsync(copy.Id);
        Assert.Equal(originalStatus, copyInDb!.Status);
        Assert.Equal(originalCondition, copyInDb.PhysicalCondition);

        // Không có bản ghi sự cố nào được thêm vào
        Assert.Empty(await _db.BookIncidents.Where(i => i.BookCopyId == copy.Id).ToListAsync());

        // Không có lịch sử chuyển trạng thái nào được thêm vào
        Assert.Empty(await _db.BookCopyStatusHistories.Where(h => h.BookCopyId == copy.Id).ToListAsync());
    }

    // 13. Xác nhận các chức năng hiện có liên quan đến phiếu mượn, bản sao sách và số lượng sách rảnh vẫn hoạt động bình thường
    [Fact]
    public async Task Test13_ExistingLoanAndCopyFunctions_ContinueToWorkNormally()
    {
        var (book, copy, reader, loan) = await SeedOpenLoanAsync();

        // 1. Kiểm tra danh sách phiếu mượn đang mở
        var openLoans = await _incidentService.GetOpenLoansAsync();
        Assert.Contains(openLoans, l => l.LoanId == loan.Id);

        // 2. Kiểm tra lấy danh sách bản sao theo phiếu mượn
        var copiesForLoan = await _incidentService.GetCopiesForLoanAsync(loan.Id);
        Assert.Single(copiesForLoan);
        Assert.Equal(copy.Id, copiesForLoan[0].CopyId);

        // 3. Thực hiện ghi nhận sự cố
        var outcome = await _incidentService.RecordIncidentAsync(new RecordIncidentRequest
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Damaged,
            Description = "Hư hỏng nặng do rách bìa và rụng trang",
            StaffEmail = "librarian@library.edu.vn"
        });
        Assert.True(outcome.IsSuccess);

        // 4. Phiếu mượn được đóng với lý do Hỏng nặng (Lát 3)
        var loanInDb = await _db.BookLoans.FindAsync(loan.Id);
        Assert.NotNull(loanInDb);
        Assert.NotNull(loanInDb.ReturnedAtUtc);
        Assert.True(loanInDb.IsReturned);
        Assert.Equal(BookIncidentType.Damaged, loanInDb.CloseReason);

        // 5. Phiếu mượn đã đóng không còn xuất hiện trong danh sách phiếu đang mở
        var openLoansAfter = await _incidentService.GetOpenLoansAsync();
        Assert.DoesNotContain(openLoansAfter, l => l.LoanId == loan.Id);

        // 6. Bản sao đã loại không cho mượn tiếp hoặc trùng lặp
        var recheckCopies = await _incidentService.GetCopiesForLoanAsync(loan.Id);
        Assert.Empty(recheckCopies);
        var recheckCopy = await _db.BookCopies.FindAsync(copy.Id);
        Assert.Equal(BookCopyStatus.Removed, recheckCopy!.Status);
    }

    // Test bổ sung: Kiểm tra Controller hoạt động chuẩn xác
    [Fact]
    public async Task Test14_BookIncidentController_IndexAndRecord_WorkCorrectly()
    {
        var (_, copy, _, loan) = await SeedOpenLoanAsync();
        var auditMock = new AuditLogService(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditLogService>.Instance);
        var controller = new BookIncidentController(_incidentService, auditMock)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new FakeTempDataProvider())
        };

        // GET Index
        var indexResult = await controller.Index(loan.Id) as ViewResult;
        Assert.NotNull(indexResult);
        var indexModel = indexResult.Model as BookIncidentIndexViewModel;
        Assert.NotNull(indexModel);
        Assert.NotEmpty(indexModel.OpenLoans);
        Assert.Single(indexModel.SelectedLoanCopies);

        // POST Record Valid
        var postModel = new RecordBookIncidentViewModel
        {
            LoanId = loan.Id,
            BookCopyId = copy.Id,
            IncidentType = BookIncidentType.Lost,
            Description = "Đã báo mất trực tiếp tại quầy"
        };

        var postResult = await controller.Record(postModel) as RedirectToActionResult;
        Assert.NotNull(postResult);
        Assert.Equal(nameof(BookIncidentController.Index), postResult.ActionName);
        Assert.NotNull(controller.TempData["SuccessMessage"]);

        // POST Record Lặp lại (phải báo lỗi)
        var duplicateResult = await controller.Record(postModel) as ViewResult;
        Assert.NotNull(duplicateResult);
        Assert.NotNull(controller.TempData["ErrorMessage"]);
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
