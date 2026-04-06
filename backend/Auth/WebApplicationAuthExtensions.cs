using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Auth;

public static class WebApplicationAuthExtensions
{
    private const string DisableWebdavAuthEnvVar = "DISABLE_WEBDAV_AUTH";
    private const string RequiredConfirmation = "I_UNDERSTAND_THIS_IS_INSECURE";

    private const string DisabledWebdavAuthLog =
        "WebDAV authentication is DISABLED via the DISABLE_WEBDAV_AUTH environment variable";

    private static readonly Action<Action> LogOnlyOnce = DebounceUtil.RunOnlyOnce();

    public static bool IsWebdavAuthDisabled()
    {
        var value = EnvironmentUtil.GetEnvironmentVariable(DisableWebdavAuthEnvVar);
        if (string.IsNullOrEmpty(value)) return false;

        if (value == RequiredConfirmation)
        {
            LogOnlyOnce(() => Log.Warning(DisabledWebdavAuthLog));
            return true;
        }

        Log.Error(
            "DISABLE_WEBDAV_AUTH is set to '{Value}' but requires the exact value '{Required}' to take effect. " +
            "WebDAV authentication remains ENABLED.",
            value, RequiredConfirmation);
        return false;
    }

    public static void UseWebdavBasicAuthentication(this WebApplication app)
    {
        if (IsWebdavAuthDisabled()) return;
        app.UseWhen(
            context => ShouldUseWebdavBasicAuthentication(context.Request.Path),
            branch => branch.UseAuthentication());
    }

    public static bool ShouldUseWebdavBasicAuthentication(PathString path)
    {
        return !path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase)
               && !path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
               && !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
               && !path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase);
    }
}
