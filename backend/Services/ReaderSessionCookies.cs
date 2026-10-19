using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Project.Models;

namespace Project.Services;

/// <summary>
/// Reader session cookies stamped with <see cref="ReaderAccount.SessionVersion"/>, so a password reset
/// invalidates every session that was signed in before it.
/// </summary>
public static class ReaderSessionCookies
{
    private const string ReaderIdCookie = "reader_id";
    private const string SessionVersionCookie = "reader_session_version";
    private const string ProtectorPurpose = "Project.ReaderSession";

    public static void Append(HttpContext context, IDataProtectionProvider dataProtectionProvider, ReaderAccount reader)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        };
        context.Response.Cookies.Append(ReaderIdCookie, reader.Id.ToString(), options);
        var sessionStamp = dataProtectionProvider.CreateProtector(ProtectorPurpose)
            .Protect($"{reader.Id}:{reader.SessionVersion}");
        context.Response.Cookies.Append(SessionVersionCookie, sessionStamp, options);
    }

    /// <returns>The signed-in reader id, 0 when no reader is signed in, or -1 when the session was rejected.</returns>
    public static async Task<int> GetReaderIdAsync(
        HttpContext context,
        IDataProtectionProvider dataProtectionProvider,
        Func<int, CancellationToken, Task<ReaderAccount?>> findReader,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (!request.Cookies.TryGetValue(ReaderIdCookie, out var idValue)) return 0;
        if (!int.TryParse(idValue, out var id))
        {
            Clear(context.Response);
            return -1;
        }

        // reader_id là cookie thường nên có thể bị sửa tay; chỉ tin khi có kèm chữ ký phiên (Append luôn ghi cả hai).
        if (!request.Cookies.TryGetValue(SessionVersionCookie, out var stamp))
        {
            Clear(context.Response);
            return -1;
        }

        int sessionVersion;
        try
        {
            var unprotected = dataProtectionProvider.CreateProtector(ProtectorPurpose).Unprotect(stamp);
            var values = unprotected.Split(':', 2);
            if (values.Length != 2 || !int.TryParse(values[0], out var stampedReaderId) || stampedReaderId != id ||
                !int.TryParse(values[1], out sessionVersion))
            {
                Clear(context.Response);
                return -1;
            }
        }
        catch (CryptographicException)
        {
            Clear(context.Response);
            return -1;
        }

        var reader = await findReader(id, cancellationToken);
        if (reader is null || reader.IsLocked || reader.SessionVersion != sessionVersion)
        {
            Clear(context.Response);
            return -1;
        }
        return id;
    }

    public static void Clear(HttpResponse response)
    {
        response.Cookies.Delete(ReaderIdCookie);
        response.Cookies.Delete(SessionVersionCookie);
    }
}
