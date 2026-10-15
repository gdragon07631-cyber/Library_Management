namespace Project.Models;

/// <summary>DTO only for the permission-review API. It does not participate in authorization decisions.</summary>
public sealed record PermissionMatrixRoleDto(string Code, string Name);
public sealed record PermissionMatrixCellDto(bool Expected, bool Current, string Status, string StatusLabel);
public sealed record PermissionMatrixRowDto(string ModuleCode, string ModuleName, string PermissionCode, string PermissionName, IReadOnlyDictionary<string, PermissionMatrixCellDto> Roles);
public sealed record PermissionMatrixDto(IReadOnlyList<PermissionMatrixRoleDto> Roles, IReadOnlyList<PermissionMatrixRowDto> Rows, int DifferenceCount, DateTime GeneratedAtUtc);
