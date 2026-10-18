using Project;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Project.Data;
using Project.Models;
using Project.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = FrontendPaths.WebRoot(Directory.GetCurrentDirectory())
});
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPasswordHasher<AdminAccount>, PasswordHasher<AdminAccount>>();
builder.Services.AddScoped<IReaderRegistrationService, ReaderRegistrationService>();
builder.Services.AddScoped<IReaderPasswordResetService, ReaderPasswordResetService>();
builder.Services.AddScoped<IReaderEmailVerificationService, ReaderEmailVerificationService>();
builder.Services.AddScoped<SmtpEmailSender>();
builder.Services.AddScoped<IHoldNotificationEmailTransport>(services => services.GetRequiredService<SmtpEmailSender>());
builder.Services.AddScoped<IEmailSender>(services => new RetryingEmailSender(
    services.GetRequiredService<SmtpEmailSender>(),
    services.GetRequiredService<ApplicationDbContext>(),
    services.GetRequiredService<IConfiguration>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<RetryingEmailSender>>()));
builder.Services.AddSingleton<ReaderRegistrationIpRateLimiter>();
builder.Services.AddScoped<IPasswordHasher<ReaderAccount>, PasswordHasher<ReaderAccount>>();
builder.Services.AddScoped<IAuthorService, AuthorService>();
builder.Services.AddScoped<IBookService, BookService>();
builder.Services.AddScoped<IBookCoverThumbnailService, BookCoverThumbnailService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IWarehouseService, WarehouseService>();
builder.Services.AddScoped<IShelfService, ShelfService>();
builder.Services.AddScoped<IWorkingScheduleService, WorkingScheduleService>();
builder.Services.AddScoped<IBookLoanService, BookLoanService>();
builder.Services.AddScoped<ILibraryReportService, LibraryReportService>();
builder.Services.AddScoped<ILoanReportService, LoanReportService>();
builder.Services.AddScoped<IFeeWaiverService, FeeWaiverService>();
builder.Services.AddScoped<IFeePaymentService, FeePaymentService>();
builder.Services.AddScoped<FeeReceiptService>();
builder.Services.AddScoped<IBookIncidentService, BookIncidentService>();
builder.Services.AddScoped<ICompensationService, CompensationService>();
builder.Services.AddScoped<DirectLoanService>();
builder.Services.AddScoped<OverdueReaderExportService>();
builder.Services.AddScoped<ILoanContactHistoryService, LoanContactHistoryService>();
builder.Services.AddScoped<ILoanDueReminderService, LoanDueReminderService>();
builder.Services.AddScoped<IBookLoanDetailsService, BookLoanDetailsService>();
builder.Services.AddScoped<ILoanLookupService, LoanLookupService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<IStaffAccountService, StaffAccountService>();
builder.Services.AddScoped<IBookCopyService, BookCopyService>();
builder.Services.AddScoped<IHoldPickupService, HoldPickupService>();
builder.Services.AddScoped<IHoldPickupConfirmationService, HoldPickupConfirmationService>();
builder.Services.AddScoped<IBookHoldQueueService, BookHoldQueueService>();
builder.Services.AddScoped<IBookHoldFulfillmentService, BookHoldFulfillmentService>();
builder.Services.AddScoped<IStaffHoldCancellationService, StaffHoldCancellationService>();
builder.Services.AddScoped<IHoldPickupExpiryService, HoldPickupExpiryService>();
builder.Services.AddScoped<IHoldPickupExpirySchedulingService, HoldPickupExpirySchedulingService>();
builder.Services.AddScoped<IHoldPickupExpiryHistoryService, HoldPickupExpiryHistoryService>();
builder.Services.AddScoped<HoldPickupReminderEnqueuer>();
builder.Services.AddScoped<HoldEmailOutboxProcessor>();
builder.Services.AddScoped<IReaderAccountAdminService, ReaderAccountAdminService>();
builder.Services.AddSingleton<IPermissionMatrixService, PermissionMatrixService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<HoldPickupExpiryScheduledWorker>();
<<<<<<< HEAD
builder.Services.AddHostedService<HoldReadyEmailWorker>();
builder.Services.AddOptions<HoldPickupReminderOptions>()
    .Bind(builder.Configuration.GetSection(HoldPickupReminderOptions.SectionName))
    .Validate(options => HoldPickupReminderSchedule.TryParseRunTime(options.SendTimeLocal, out _),
        "HoldPickupReminder:SendTimeLocal must use HH:mm in Asia/Ho_Chi_Minh.")
    .Validate(options => options.RetryIntervalSeconds is >= 30 and <= 3600,
        "HoldPickupReminder:RetryIntervalSeconds must be between 30 and 3600.")
    .ValidateOnStart();
builder.Services.AddHostedService<HoldPickupReminderScheduledWorker>();
=======
builder.Services.AddHostedService<LoanDueReminderWorker>();
>>>>>>> origin/feature/nhac-den-han
builder.Services.Configure<BookCopyLabelPrintOptions>(builder.Configuration.GetSection("BookCopyLabels"));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "Data", "Keys")));
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

var createLibrarian = args.Contains("--create-librarian", StringComparer.OrdinalIgnoreCase);
if (createLibrarian || args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AdminAccount>>();

    Console.Write(createLibrarian ? "Email thủ thư: " : "Email quản trị: ");
    var email = Console.ReadLine()?.Trim();
    Console.Write(createLibrarian ? "Mật khẩu thủ thư: " : "Mật khẩu quản trị: ");
    var password = ReadPassword();
    Console.WriteLine();

    if (string.IsNullOrWhiteSpace(email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email) ||
        string.IsNullOrEmpty(password))
    {
        Console.Error.WriteLine("Email hoặc mật khẩu không hợp lệ.");
        return;
    }

    await dbContext.Database.MigrateAsync();
    var normalizedEmail = email.ToUpperInvariant();
    if (await dbContext.AdminAccounts.AnyAsync(account => account.Email.ToUpper() == normalizedEmail))
    {
        Console.Error.WriteLine("Tài khoản email này đã tồn tại.");
        return;
    }

    var account = new AdminAccount
    {
        Email = email,
        IsActive = true,
        Role = createLibrarian ? AccountRoles.Librarian : AccountRoles.SystemAdmin
    };
    account.PasswordHash = passwordHasher.HashPassword(account, password);
    dbContext.AdminAccounts.Add(account);
    dbContext.AuditLogs.Add(AuditLogService.Create(
        $"Hệ thống (dòng lệnh, {Environment.UserName})",
        AuditActions.CreateAccount,
        $"Tài khoản {AuditLogService.DescribeRole(account.Role)} ({account.Email})",
        "127.0.0.1"));
    await dbContext.SaveChangesAsync();
    Console.WriteLine($"Đã tạo tài khoản {(createLibrarian ? "thủ thư" : "quản trị")}. Mật khẩu không được lưu dạng plaintext.");
    return;
}

if (args.Contains("--migrate-database", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
    Console.WriteLine("Database migrations applied successfully.");
    return;
}

if (args.Contains("--seed-library-demo", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var seeder = new LibraryDemoDataSeeder(dbContext);
    var result = await seeder.SeedAsync();
    Console.WriteLine(
        $"Library demo seed complete. Added: authors={result.AuthorsAdded}, categories={result.CategoriesAdded}, " +
        $"warehouses={result.WarehousesAdded}, shelves={result.ShelvesAdded}, books={result.BooksAdded}, copies={result.CopiesAdded}.");
    return;
}

if (args.Contains("--seed-staging-demo", StringComparer.OrdinalIgnoreCase))
{
    StagingResetEnvironmentGuard.EnsureStaging(app.Environment);

    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var passwords = new Dictionary<string, string?>
    {
        ["SYSTEM_ADMIN"] = app.Configuration["STAGING_DEMO_SYSTEM_ADMIN_PASSWORD"],
        ["LIBRARY_MANAGER"] = app.Configuration["STAGING_DEMO_LIBRARY_MANAGER_PASSWORD"],
        ["LIBRARIAN"] = app.Configuration["STAGING_DEMO_LIBRARIAN_PASSWORD"],
        ["READER"] = app.Configuration["STAGING_DEMO_READER_PASSWORD"]
    };
    try
    {
        await dbContext.Database.MigrateAsync();
        var seeder = new StagingAcceptanceDataSeeder(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<AdminAccount>>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<ReaderAccount>>());
        var result = await seeder.SeedAsync(passwords);
        Console.WriteLine(
            $"Staging sample reset complete: books={result.Books}, copies={result.Copies}, readers={result.Readers}, loans={result.Loans}.");
        foreach (var state in result.LoanStates.OrderBy(item => item.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {state.Key}: {state.Value}");
        foreach (var account in result.DemoAccounts.OrderBy(item => item.Key, StringComparer.Ordinal))
            Console.WriteLine($"  account {account.Key}: {(account.Value ? "created" : "preserved")}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Staging sample reset failed; transaction was rolled back: {exception.Message}");
        throw;
    }
    return;
}

if (args.Contains("--seed-staging-demo-accounts", StringComparer.OrdinalIgnoreCase))
{
    StagingResetEnvironmentGuard.EnsureStaging(app.Environment);

    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
    var accountSeeder = new StagingDemoAccountsSeeder(
        dbContext,
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<AdminAccount>>(),
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<ReaderAccount>>());
    var passwords = new Dictionary<string, string?>
    {
        ["SYSTEM_ADMIN"] = app.Configuration["STAGING_DEMO_SYSTEM_ADMIN_PASSWORD"],
        ["LIBRARY_MANAGER"] = app.Configuration["STAGING_DEMO_LIBRARY_MANAGER_PASSWORD"],
        ["LIBRARIAN"] = app.Configuration["STAGING_DEMO_LIBRARIAN_PASSWORD"],
        ["READER"] = app.Configuration["STAGING_DEMO_READER_PASSWORD"]
    };
    var result = await accountSeeder.SeedAsync(passwords);
    Console.WriteLine("Staging demo accounts checked (passwords omitted):");
    foreach (var item in result.Created)
        Console.WriteLine($"  {item.Key}: {(item.Value ? "created" : "already exists; unchanged")}");
    return;
}

if (args.Contains("--seed-hold-pickup", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();

    var warehouse = await dbContext.Warehouses.FirstOrDefaultAsync(w => w.Code == "KHO-CHINH");
    if (warehouse == null)
    {
        warehouse = new Warehouse { Code = "KHO-CHINH", Name = "Kho Sách Trung Tâm", Status = WarehouseStatus.Active };
        dbContext.Warehouses.Add(warehouse);
        await dbContext.SaveChangesAsync();
    }

    var shelf = await dbContext.Shelves.FirstOrDefaultAsync(s => s.WarehouseId == warehouse.Id && s.Code == "KE-CHO-NHAN");
    if (shelf == null)
    {
        shelf = new Shelf { WarehouseId = warehouse.Id, Code = "KE-CHO-NHAN", Name = "Giá chờ nhận sách", Status = ShelfStatus.Active };
        dbContext.Shelves.Add(shelf);
        await dbContext.SaveChangesAsync();
    }

    var author = await dbContext.Authors.FirstOrDefaultAsync(a => a.Name == "Nhiều Tác Giả");
    if (author == null)
    {
        author = new Author { Name = "Nhiều Tác Giả" };
        dbContext.Authors.Add(author);
        await dbContext.SaveChangesAsync();
    }

    var bookTitles = new[] { "Lập trình C# hiện đại", "Cấu trúc dữ liệu và giải thuật", "Kiến trúc hệ thống phần mềm" };
    var books = new List<Book>();
    foreach (var title in bookTitles)
    {
        var b = await dbContext.Books.FirstOrDefaultAsync(item => item.Title == title);
        if (b == null)
        {
            b = new Book { Title = title, AuthorId = author.Id };
            dbContext.Books.Add(b);
            await dbContext.SaveChangesAsync();
        }
        books.Add(b);
    }

    var readerInfos = new[]
    {
        ("Nguyễn Văn An", "an.demo@thuvien.local", "0901234567", "BD-DEMO-01"),
        ("Trần Thị Bình", "binh.demo@thuvien.local", "0912345678", "BD-DEMO-02"),
        ("Lê Văn Cường", "cuong.demo@thuvien.local", "0923456789", "BD-DEMO-03")
    };
    var readers = new List<ReaderAccount>();
    foreach (var (name, email, phone, code) in readerInfos)
    {
        var r = await dbContext.ReaderAccounts.FirstOrDefaultAsync(item => item.StudentOrStaffCode == code);
        if (r == null)
        {
            r = new ReaderAccount
            {
                FullName = name,
                Email = email,
                PhoneNumber = phone,
                StudentOrStaffCode = code,
                DateOfBirth = new DateOnly(2000, 1, 1),
                PasswordHash = "demo",
                Status = "Đang hoạt động"
            };
            dbContext.ReaderAccounts.Add(r);
            await dbContext.SaveChangesAsync();
        }
        readers.Add(r);
    }

    var copyBarcodes = new[] { "BARCODE-HN-001", "BARCODE-D2-002", "BARCODE-D7-003" };
    var copies = new List<BookCopy>();
    for (int i = 0; i < 3; i++)
    {
        var barcode = copyBarcodes[i];
        var c = await dbContext.BookCopies.FirstOrDefaultAsync(item => item.CopyCode == barcode);
        if (c == null)
        {
            c = new BookCopy
            {
                BookId = books[i].Id,
                ShelfId = shelf.Id,
                CopyCode = barcode,
                Status = BookCopyStatus.OnHold,
                PhysicalCondition = BookCopyCondition.Good
            };
            dbContext.BookCopies.Add(c);
            await dbContext.SaveChangesAsync();
        }
        copies.Add(c);
    }

    var baseDate = DateTime.UtcNow.Date.AddHours(17);
    var deadlines = new[] { baseDate, baseDate.AddDays(2), baseDate.AddDays(7) };

    for (int i = 0; i < 3; i++)
    {
        var r = readers[i];
        var b = books[i];
        var c = copies[i];
        var dl = deadlines[i];

        var existingHold = await dbContext.BookHolds.FirstOrDefaultAsync(h => h.ReaderAccountId == r.Id && h.BookId == b.Id);
        if (existingHold == null)
        {
            existingHold = new BookHold
            {
                ReaderAccountId = r.Id,
                BookId = b.Id,
                BookCopyId = c.Id,
                Status = BookHoldStatus.Available,
                PickupDeadlineUtc = dl,
                HeldAtUtc = DateTime.UtcNow
            };
            dbContext.BookHolds.Add(existingHold);
        }
        else
        {
            existingHold.BookCopyId = c.Id;
            existingHold.Status = BookHoldStatus.Available;
            existingHold.PickupDeadlineUtc = dl;
        }
    }
    await dbContext.SaveChangesAsync();
    Console.WriteLine("Đã tạo 3 dữ liệu mẫu sách chờ nhận (Hạn: Hôm nay, +2 ngày, +7 ngày).");
    return;
}

var jwtSigningKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(jwtSigningKey) || System.Text.Encoding.UTF8.GetByteCount(jwtSigningKey) < 32)
{
    throw new InvalidOperationException(
        "Configure Jwt:SigningKey using dotnet user-secrets or the Jwt__SigningKey environment variable (minimum 32 bytes) before starting the web app.");
}

// Staging starts from an empty SQL Server volume on first launch. Apply the existing
// migrations before serving requests; other environments retain their current behavior.
if (app.Environment.IsStaging())
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseStaticFiles();

app.MapGet("/health/ready", async Task<IResult> (ApplicationDbContext dbContext, CancellationToken cancellationToken) =>
{
    if (await dbContext.Database.CanConnectAsync(cancellationToken))
        return Results.Ok(new { status = "ready" });

    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");


app.Run();

static string ReadPassword()
{
    if (Console.IsInputRedirected)
    {
        throw new InvalidOperationException("Hãy chạy lệnh này trong terminal tương tác để nhập mật khẩu an toàn.");
    }

    var password = new System.Text.StringBuilder();
    ConsoleKeyInfo key;
    do
    {
        key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Backspace && password.Length > 0)
        {
            password.Length--;
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
        }
    } while (key.Key != ConsoleKey.Enter);

    return password.ToString();
}
