using System.Text.Json;
using Project.Models;

namespace Project.Services;

/// <summary>
/// Manages the system permission matrix and compares active role permissions against the standard baseline.
/// </summary>
public sealed class PermissionMatrixService : IPermissionMatrixService
{
    public const string Reader = "Reader";
    private readonly object syncRoot = new();
    private readonly string storageFilePath;
    private Dictionary<string, HashSet<string>>? customRoleMap;

    public PermissionMatrixService(IWebHostEnvironment? environment = null)
    {
        var root = environment?.ContentRootPath ?? Directory.GetCurrentDirectory();
        storageFilePath = Path.Combine(root, "Data", "permission-matrix.json");
        LoadFromDisk();
    }

    private static readonly PermissionMatrixRoleDto[] Roles =
    [
        new(AccountRoles.SystemAdmin, "Quản trị hệ thống"),
        new(AccountRoles.LibraryManager, "Quản lý thư viện"),
        new(AccountRoles.Librarian, "Thủ thư"),
        new(Reader, "Bạn đọc")
    ];

    // Standard matrix baseline. Entries omitted from a role are denied by default.
    private static readonly Definition[] Definitions =
    [
        new("catalog", "Danh mục sách", "Read", "Xem / tra cứu sách", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("catalog", "Danh mục sách", "Create", "Biên mục / thêm bản sao", "SystemAdmin,LibraryManager,Librarian"),
        new("catalog", "Danh mục sách", "Update", "Cập nhật thông tin sách & bản sao", "SystemAdmin,LibraryManager,Librarian"),
        new("catalog", "Danh mục sách", "Delete", "Xóa / thanh lý bản sao", "SystemAdmin,LibraryManager"),
        new("catalog", "Danh mục sách", "Export", "In nhãn mã vạch / xuất dữ liệu", "SystemAdmin,LibraryManager,Librarian"),
        new("inventory", "Kho và kệ sách", "Read", "Xem danh sách kho & kệ", "SystemAdmin,LibraryManager"),
        new("inventory", "Kho và kệ sách", "Create", "Tạo mới kho & kệ", "SystemAdmin,LibraryManager"),
        new("inventory", "Kho và kệ sách", "Update", "Cập nhật kho & kệ", "SystemAdmin,LibraryManager"),
        new("inventory", "Kho và kệ sách", "Delete", "Ngừng sử dụng / xóa kho & kệ", "SystemAdmin,LibraryManager"),
        new("circulation", "Mượn trả sách", "Read", "Xem / tra cứu phiếu mượn & lịch sử", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("circulation", "Mượn trả sách", "Create", "Lập phiếu mượn trực tiếp / tại quầy", "SystemAdmin,LibraryManager,Librarian"),
        new("circulation", "Mượn trả sách", "Update", "Nhận trả sách / gia hạn", "SystemAdmin,LibraryManager,Librarian"),
        new("incidents", "Sách mất / hỏng & Bồi thường", "Create", "Ghi nhận sự cố sách mất / hỏng", "SystemAdmin,LibraryManager,Librarian"),
        new("incidents", "Sách mất / hỏng & Bồi thường", "Update", "Xử lý / thanh lý tài sản mất hỏng", "SystemAdmin,LibraryManager"),
        new("holds", "Đặt giữ sách", "Read", "Xem hàng đợi & đơn đặt giữ", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("holds", "Đặt giữ sách", "Create", "Đặt giữ sách (kể cả khi hết bản rảnh)", "Reader"),
        new("holds", "Đặt giữ sách", "Update", "Xác nhận nhận sách / hủy đặt giữ", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("fees", "Thu phí & Miễn giảm", "Create", "Thu phí phạt / lập đề nghị miễn giảm", "Librarian"),
        new("fees", "Thu phí & Miễn giảm", "Approve", "Duyệt đề nghị miễn giảm phí", "LibraryManager"),
        new("readers", "Tài khoản & Thẻ bạn đọc", "Read", "Xem hồ sơ & tra cứu thẻ bạn đọc", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("readers", "Tài khoản & Thẻ bạn đọc", "Update", "Duyệt thẻ / Khóa & Mở khóa thẻ bạn đọc", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("staff", "Tài khoản nhân sự", "Read", "Xem danh sách nhân sự", "SystemAdmin"),
        new("staff", "Tài khoản nhân sự", "Create", "Tạo tài khoản nhân sự mới", "SystemAdmin"),
        new("staff", "Tài khoản nhân sự", "Update", "Phân vai trò / Khóa & Mở khóa nhân sự", "SystemAdmin"),
        new("policy", "Chính sách & Lịch làm việc", "Read", "Xem chính sách mượn, bồi thường & lịch", "SystemAdmin,LibraryManager"),
        new("policy", "Chính sách & Lịch làm việc", "Update", "Cập nhật chính sách & lịch làm việc", "SystemAdmin,LibraryManager"),
        new("audit", "Nhật ký hoạt động", "Read", "Xem nhật ký kiểm toán hệ thống", "SystemAdmin,LibraryManager"),
        new("reports", "Báo cáo thống kê", "Export", "Xem báo cáo & Xuất Excel danh sách quá hạn", "SystemAdmin,LibraryManager"),
        new("matrix", "Ma trận phân quyền", "Read", "Xem & đối chiếu ma trận phân quyền", "SystemAdmin"),
        new("matrix", "Ma trận phân quyền", "Update", "Cấu hình & cập nhật ma trận phân quyền", "SystemAdmin")
    ];

    public PermissionMatrixDto GetReview()
    {
        lock (syncRoot)
        {
            var rows = Definitions.Select(definition =>
            {
                var expected = ParseRoles(definition.ExpectedRoles);
                var current = GetCurrentRolesLocked(definition, expected);
                var cells = Roles.ToDictionary(
                    role => role.Code,
                    role => CreateCell(expected.Contains(role.Code), current.Contains(role.Code)));
                return new PermissionMatrixRowDto(
                    definition.ModuleCode,
                    definition.ModuleName,
                    definition.PermissionCode,
                    definition.PermissionName,
                    cells);
            }).ToList();

            var diffCount = rows.Sum(row => row.Roles.Values.Count(cell => cell.Status != "match"));
            return new PermissionMatrixDto(Roles, rows, diffCount, DateTime.UtcNow);
        }
    }

    public void UpdatePermissions(IReadOnlyCollection<string> grantedKeys)
    {
        lock (syncRoot)
        {
            var newMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in Definitions)
            {
                var key = MakeDefinitionKey(definition.ModuleCode, definition.PermissionCode);
                newMap[key] = new HashSet<string>(StringComparer.Ordinal);
            }

            if (grantedKeys != null)
            {
                foreach (var entry in grantedKeys)
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    var parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length != 3) continue;
                    var defKey = MakeDefinitionKey(parts[0], parts[1]);
                    var roleCode = parts[2];
                    if (newMap.TryGetValue(defKey, out var roleSet) && Roles.Any(r => r.Code == roleCode))
                    {
                        roleSet.Add(roleCode);
                    }
                }
            }

            // Ensure SystemAdmin always retains matrix Read & Update so they never lock themselves out
            newMap[MakeDefinitionKey("matrix", "Read")].Add(AccountRoles.SystemAdmin);
            newMap[MakeDefinitionKey("matrix", "Update")].Add(AccountRoles.SystemAdmin);

            customRoleMap = newMap;
            SaveToDisk();
        }
    }

    public void ResetToDefault()
    {
        lock (syncRoot)
        {
            customRoleMap = null;
            try
            {
                if (File.Exists(storageFilePath))
                {
                    File.Delete(storageFilePath);
                }
            }
            catch
            {
                // Ignore disk cleanup errors
            }
        }
    }

    public bool HasPermission(string role, string moduleCode, string permissionCode)
    {
        lock (syncRoot)
        {
            var definition = Definitions.FirstOrDefault(d =>
                string.Equals(d.ModuleCode, moduleCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.PermissionCode, permissionCode, StringComparison.OrdinalIgnoreCase));
            if (definition == null) return false;
            var expected = ParseRoles(definition.ExpectedRoles);
            var current = GetCurrentRolesLocked(definition, expected);
            return current.Contains(role);
        }
    }

    private HashSet<string> GetCurrentRolesLocked(Definition definition, HashSet<string> expected)
    {
        var key = MakeDefinitionKey(definition.ModuleCode, definition.PermissionCode);
        if (customRoleMap != null && customRoleMap.TryGetValue(key, out var configuredRoles))
        {
            return new HashSet<string>(configuredRoles, StringComparer.Ordinal);
        }

        return new HashSet<string>(expected, StringComparer.Ordinal);
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(storageFilePath)) return;
            var json = File.ReadAllText(storageFilePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            if (raw == null) return;
            customRoleMap = raw.ToDictionary(
                kvp => kvp.Key,
                kvp => new HashSet<string>(kvp.Value ?? [], StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            customRoleMap = null;
        }
    }

    private void SaveToDisk()
    {
        try
        {
            if (customRoleMap == null) return;
            var dir = Path.GetDirectoryName(storageFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var serializable = customRoleMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
            var json = JsonSerializer.Serialize(serializable, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(storageFilePath, json);
        }
        catch
        {
            // Ignore file persistence errors in restricted environments
        }
    }

    private static string MakeDefinitionKey(string moduleCode, string permissionCode) =>
        $"{moduleCode}:{permissionCode}";

    private static PermissionMatrixCellDto CreateCell(bool expected, bool current) =>
        expected == current ? new(expected, current, "match", current ? "Có quyền" : "Không có quyền") :
        current ? new(expected, current, "excess", "Thừa quyền") :
        new(expected, current, "missing", "Thiếu quyền");

    private static HashSet<string> ParseRoles(string roles) =>
        roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .ToHashSet(StringComparer.Ordinal);

    private sealed record Definition(string ModuleCode, string ModuleName, string PermissionCode, string PermissionName, string ExpectedRoles);
}
