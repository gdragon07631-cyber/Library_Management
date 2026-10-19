using Project.Models;

namespace Project.Services;

public interface IPermissionMatrixService
{
    PermissionMatrixDto GetReview();
    void UpdatePermissions(IReadOnlyCollection<string> grantedKeys);
    void ResetToDefault();
    bool HasPermission(string role, string moduleCode, string permissionCode);
}
