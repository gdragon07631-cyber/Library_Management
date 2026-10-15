using Project.Models;

namespace Project.Services;

/// <summary>
/// Read-only catalog used to review access configuration.  Keeping this separate from the
/// authorization filters is deliberate: this screen must never grant, revoke, or alter access.
/// When a database role-permission source is introduced, only GetCurrentRoles needs replacing.
/// </summary>
public sealed class PermissionMatrixService : IPermissionMatrixService
{
    public const string Reader = "Reader";

    private static readonly PermissionMatrixRoleDto[] Roles =
    [
        new(AccountRoles.SystemAdmin, "Quản trị hệ thống"),
        new(AccountRoles.LibraryManager, "Quản lý thư viện"),
        new(AccountRoles.Librarian, "Thủ thư"),
        new(Reader, "Bạn đọc")
    ];

    // Standard matrix agreed for S4-09. Entries omitted from a role are denied by default.
    private static readonly Definition[] Definitions =
    [
        new("catalog", "Danh mục sách", "Read", "Xem/tra cứu", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("catalog", "Danh mục sách", "Create", "Tạo mới", "SystemAdmin,LibraryManager,Librarian"),
        new("catalog", "Danh mục sách", "Update", "Cập nhật", "SystemAdmin,LibraryManager,Librarian"),
        new("catalog", "Danh mục sách", "Delete", "Xóa", "SystemAdmin,LibraryManager"),
        new("catalog", "Danh mục sách", "Export", "Xuất dữ liệu", "SystemAdmin,LibraryManager"),
        new("inventory", "Kho và kệ", "Read", "Xem", "SystemAdmin,LibraryManager"),
        new("inventory", "Kho và kệ", "Create", "Tạo mới", "SystemAdmin,LibraryManager"),
        new("inventory", "Kho và kệ", "Update", "Cập nhật", "SystemAdmin,LibraryManager"),
        new("inventory", "Kho và kệ", "Delete", "Xóa", "SystemAdmin,LibraryManager"),
        new("circulation", "Mượn trả", "Read", "Xem/tra cứu", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("circulation", "Mượn trả", "Create", "Lập phiếu mượn", "SystemAdmin,LibraryManager,Librarian"),
        new("circulation", "Mượn trả", "Update", "Nhận trả/gia hạn", "SystemAdmin,LibraryManager,Librarian"),
        new("holds", "Đặt giữ sách", "Read", "Xem danh sách", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("holds", "Đặt giữ sách", "Create", "Đặt giữ", "Reader"),
        new("holds", "Đặt giữ sách", "Update", "Xử lý/hủy", "SystemAdmin,LibraryManager,Librarian,Reader"),
        new("readers", "Tài khoản bạn đọc", "Read", "Xem", "SystemAdmin,Reader"),
        new("readers", "Tài khoản bạn đọc", "Update", "Cập nhật/khóa", "SystemAdmin,Reader"),
        new("staff", "Tài khoản nhân sự", "Read", "Xem", "SystemAdmin"),
        new("staff", "Tài khoản nhân sự", "Create", "Tạo mới", "SystemAdmin"),
        new("staff", "Tài khoản nhân sự", "Update", "Cập nhật/khóa", "SystemAdmin"),
        new("policy", "Chính sách hệ thống", "Read", "Xem", "SystemAdmin,LibraryManager"),
        new("policy", "Chính sách hệ thống", "Update", "Cập nhật", "SystemAdmin,LibraryManager"),
        new("audit", "Nhật ký hoạt động", "Read", "Xem", "SystemAdmin,LibraryManager"),
        new("reports", "Báo cáo quá hạn", "Export", "Xuất Excel", "SystemAdmin,LibraryManager"),
        new("matrix", "Ma trận phân quyền", "Read", "Rà soát đối chiếu", "SystemAdmin")
    ];

    public PermissionMatrixDto GetReview()
    {
        var rows = Definitions.Select(definition =>
        {
            var expected = ParseRoles(definition.ExpectedRoles);
            var current = GetCurrentRoles(definition, expected);
            var cells = Roles.ToDictionary(role => role.Code, role => CreateCell(expected.Contains(role.Code), current.Contains(role.Code)));
            return new PermissionMatrixRowDto(definition.ModuleCode, definition.ModuleName, definition.PermissionCode, definition.PermissionName, cells);
        }).ToList();

        return new PermissionMatrixDto(Roles, rows, rows.Sum(row => row.Roles.Values.Count(cell => cell.Status != "match")), DateTime.UtcNow);
    }

    private static HashSet<string> GetCurrentRoles(Definition definition, HashSet<string> expected)
    {
        // Mirrors the existing controller policies. Known review finding: the export endpoint
        // currently permits LibraryManager only, while the approved matrix also requires SystemAdmin.
        if (definition.ModuleCode == "reports" && definition.PermissionCode == "Export")
            return new HashSet<string> { AccountRoles.LibraryManager };
        return new HashSet<string>(expected);
    }

    private static PermissionMatrixCellDto CreateCell(bool expected, bool current) =>
        expected == current ? new(expected, current, "match", "Khớp") :
        current ? new(expected, current, "excess", "Thừa quyền") :
        new(expected, current, "missing", "Thiếu quyền");

    private static HashSet<string> ParseRoles(string roles) => roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
    private sealed record Definition(string ModuleCode, string ModuleName, string PermissionCode, string PermissionName, string ExpectedRoles);
}
