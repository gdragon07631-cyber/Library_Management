using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Project.Controllers;
using Project.Data;
using Project.Filters;
using Project.Models;
using Project.Services;
using Xunit.Abstractions;

namespace Project.Tests;

/// <summary>Regression gate for every staff-only business API.</summary>
public sealed class PermissionApiAuthorizationTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly AuditLogService audit;
    private readonly ITestOutputHelper output;

    public PermissionApiAuthorizationTests(ITestOutputHelper output)
    {
        this.output = output;
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        audit = new AuditLogService(db, NullLogger<AuditLogService>.Instance);
    }

    [Fact]
    public void EveryStaffBusinessApiHasAnAuthorizationGuard()
    {
        var unguarded = ApiActions().Where(x => x.Controller.GetCustomAttribute<StaffOnlyAttribute>() is null)
            .Where(x => x.Controller != typeof(CatalogController))
            .Where(x => x.Controller != typeof(ReaderRegistrationController) && x.Controller != typeof(ReaderLoanController))
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}").ToArray();
        Assert.Empty(unguarded);
    }

    [Fact]
    public async Task ProtectedApisReturn401Or403AndAllowOnlyConfiguredStaffRoles()
    {
        var endpoints = ApiActions().Where(x => x.Controller.GetCustomAttribute<StaffOnlyAttribute>() is not null).ToArray();
        var passed = 0;
        var failures = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var guard = endpoint.Controller.GetCustomAttribute<StaffOnlyAttribute>()!;
            var anonymous = await RunGuardAsync(guard, endpoint.Template, null);
            if (anonymous is UnauthorizedObjectResult) passed++; else failures.Add($"{endpoint.Template}: anonymous != 401");
            foreach (var role in new[] { AccountRoles.SystemAdmin, AccountRoles.LibraryManager, AccountRoles.Librarian })
            {
                var result = await RunGuardAsync(guard, endpoint.Template, await SignInStaffAsync(role));
                var allowed = guard.Roles.Contains(role);
                if (allowed && result is null) passed++;
                else if (!allowed && result is ObjectResult denied && denied.StatusCode == StatusCodes.Status403Forbidden) passed++;
                else failures.Add($"{endpoint.Template}: {role} did not receive expected {(allowed ? "access" : "403")}");
            }
        }
        output.WriteLine(JsonSerializer.Serialize(new { report = "permission-api-audit", totalApiEndpoints = endpoints.Length, checks = endpoints.Length * 4, passed, failed = failures.Count, failures }));
        Assert.Empty(failures);
    }

    private static IEnumerable<(Type Controller, MethodInfo Action, string Template)> ApiActions() =>
        typeof(BookController).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(Controller).IsAssignableFrom(t))
            .SelectMany(controller => controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .SelectMany(action => action.GetCustomAttributes<HttpMethodAttribute>()
                    .Where(a => a.Template?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true || a.Template?.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(a => (controller, action, a.Template!))));

    private async Task<IActionResult?> RunGuardAsync(StaffOnlyAttribute guard, string path, string? cookie)
    {
        var services = new ServiceCollection().AddSingleton<IAuditLogService>(audit).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Path = path;
        if (cookie is not null) http.Request.Headers.Cookie = cookie;
        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [], new Dictionary<string, object?>(), new object());
        await guard.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], new object())));
        return context.Result;
    }

    private async Task<string> SignInStaffAsync(string role)
    {
        var token = Guid.NewGuid().ToString("N");
        db.RefreshTokens.Add(new RefreshToken { AdminAccount = new AdminAccount { Email = $"{Guid.NewGuid():N}@test.local", Role = role, PasswordHash = "test", IsActive = true }, TokenHash = TokenService.HashRefreshToken(token), CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        return $"admin_refresh={token}";
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); }
}
