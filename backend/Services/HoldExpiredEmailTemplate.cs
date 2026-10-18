using System.Net;

namespace Project.Services;

public static class HoldExpiredEmailTemplate
{
    public static string Build(string readerName, long holdId, string bookTitle, DateTime pickupDeadlineUtc,
        DateTime cancelledAtUtc, string cancellationReason)
    {
        var deadline = HoldPickupExpirySchedule.GetVietnamLocalTime(pickupDeadlineUtc).ToString("dd/MM/yyyy HH:mm");
        var cancelledAt = HoldPickupExpirySchedule.GetVietnamLocalTime(cancelledAtUtc).ToString("dd/MM/yyyy HH:mm");
        var reason = cancellationReason == HoldPickupExpiryService.ExpiredReason
            ? "Qu\u00E1 h\u1EA1n nh\u1EADn s\u00E1ch theo quy \u0111\u1ECBnh c\u1EE7a th\u01B0 vi\u1EC7n."
            : cancellationReason;
        return $"<p>Xin ch\u00E0o {WebUtility.HtmlEncode(readerName)},</p>" +
            "<p>Th\u01B0 vi\u1EC7n xin th\u00F4ng b\u00E1o \u0111\u01A1n \u0111\u1EB7t gi\u1EEF s\u00E1ch c\u1EE7a b\u1EA1n \u0111\u00E3 b\u1ECB h\u1EE7y do qu\u00E1 th\u1EDDi h\u1EA1n nh\u1EADn s\u00E1ch.</p>" +
            "<ul>" +
            $"<li>M\u00E3 \u0111\u01A1n: {holdId}</li><li>T\u00EAn s\u00E1ch: {WebUtility.HtmlEncode(bookTitle)}</li>" +
            $"<li>H\u1EA1n cu\u1ED1i nh\u1EADn s\u00E1ch: {deadline} (gi\u1EDD Vi\u1EC7t Nam)</li>" +
            $"<li>Th\u1EDDi \u0111i\u1EC3m h\u1EE7y: {cancelledAt} (gi\u1EDD Vi\u1EC7t Nam)</li>" +
            $"<li>L\u00FD do h\u1EE7y: {WebUtility.HtmlEncode(reason)}</li></ul>" +
            "<p>N\u1EBFu v\u1EABn mu\u1ED1n \u0111\u1ECDc s\u00E1ch, b\u1EA1n c\u00F3 th\u1EC3 \u0111\u1EB7t gi\u1EEF l\u1EA1i nh\u01B0 sau:</p>" +
            "<ol><li>\u0110\u0103ng nh\u1EADp t\u00E0i kho\u1EA3n b\u1EA1n \u0111\u1ECDc.</li>" +
            "<li>M\u1EDF m\u1EE5c Tra c\u1EE9u s\u00E1ch (Catalog) v\u00E0 t\u00ECm t\u00EAn s\u00E1ch.</li>" +
            "<li>M\u1EDF chi ti\u1EBFt s\u00E1ch, ki\u1EC3m tra t\u00ECnh tr\u1EA1ng v\u00E0 ch\u1ECDn \u0111\u1EB7t gi\u1EEF n\u1EBFu h\u1EC7 th\u1ED1ng cho ph\u00E9p.</li>" +
            "<li>Theo d\u00F5i \u0111\u01A1n m\u1EDBi trong m\u1EE5c \u0111\u01A1n \u0111\u1EB7t gi\u1EEF v\u00E0 email t\u1EEB th\u01B0 vi\u1EC7n.</li></ol>" +
            "<p>Vi\u1EC7c \u0111\u1EB7t gi\u1EEF l\u1EA1i t\u00F9y thu\u1ED9c t\u00ECnh tr\u1EA1ng s\u00E1ch v\u00E0 \u0111i\u1EC1u ki\u1EC7n c\u1EE7a t\u00E0i kho\u1EA3n.</p>" +
            "<p>Tr\u00E2n tr\u1ECDng,<br/>Th\u01B0 vi\u1EC7n.</p>";
    }
}
