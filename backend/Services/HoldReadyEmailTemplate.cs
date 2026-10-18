using System.Net;

namespace Project.Services;

public static class HoldReadyEmailTemplate
{
    public static string Build(bool isLastDayReminder, string readerName, long holdId, string bookTitle,
        DateTime pickupDeadlineUtc, string pickupLocation)
    {
        var deadline = HoldPickupExpirySchedule.GetVietnamLocalTime(pickupDeadlineUtc).ToString("dd/MM/yyyy HH:mm");
        var introduction = isLastDayReminder
            ? "H\u00F4m nay l\u00E0 ng\u00E0y cu\u1ED1i c\u00F9ng \u0111\u1EC3 nh\u1EADn s\u00E1ch theo th\u1EDDi h\u1EA1n gi\u1EEF hi\u1EC7n t\u1EA1i."
            : "S\u00E1ch b\u1EA1n \u0111\u00E3 \u0111\u1EB7t gi\u1EEF hi\u1EC7n \u0111\u00E3 s\u1EB5n s\u00E0ng \u0111\u1EC3 nh\u1EADn.";
        return $"<p>Xin ch\u00E0o {WebUtility.HtmlEncode(readerName)},</p><p>{introduction}</p>" +
            "<ul>" +
            $"<li>T\u00EAn s\u00E1ch: {WebUtility.HtmlEncode(bookTitle)}</li>" +
            $"<li>M\u00E3 \u0111\u01A1n \u0111\u1EB7t gi\u1EEF: {holdId}</li>" +
            $"<li>H\u1EA1n cu\u1ED1i nh\u1EADn s\u00E1ch: {deadline} (gi\u1EDD Vi\u1EC7t Nam)</li>" +
            $"<li>\u0110\u1ECBa \u0111i\u1EC3m nh\u1EADn s\u00E1ch: {WebUtility.HtmlEncode(pickupLocation)}</li></ul>" +
            (isLastDayReminder
                ? "<p>Vui l\u00F2ng \u0111\u1EBFn nh\u1EADn s\u00E1ch tr\u01B0\u1EDBc h\u1EA1n cu\u1ED1i n\u00EAu tr\u00EAn.</p>"
                : "<p>Vui l\u00F2ng \u0111\u1EBFn nh\u1EADn s\u00E1ch tr\u01B0\u1EDBc h\u1EA1n cu\u1ED1i n\u00EAu tr\u00EAn.</p>") +
            "<p>Tr\u00E2n tr\u1ECDng,<br/>Th\u01B0 vi\u1EC7n.</p>";
    }
}
