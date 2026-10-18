using Microsoft.Extensions.Hosting;

namespace Project.Services;

public static class StagingResetEnvironmentGuard
{
    public static void EnsureStaging(IHostEnvironment environment)
    {
        if (!environment.IsStaging())
            throw new InvalidOperationException("Lệnh reset dữ liệu mẫu chỉ được chạy khi ASPNETCORE_ENVIRONMENT=Staging.");
    }
}
