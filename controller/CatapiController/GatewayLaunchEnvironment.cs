using System.Diagnostics;

namespace CatapiController;

internal static class GatewayLaunchEnvironment
{
    internal static void Apply(ProcessStartInfo info, ControllerSettings settings,
        string userMis, string? pipeName, string? pipeNonce)
    {
        info.Environment["CATPAW_BASE_URL"] = "https://catpaw.meituan.com";
        info.Environment["CATPAW_UPSTREAM_URL"] = "https://catpaw.meituan.com/api/gpt/openai/stream";
        info.Environment["CATPAW_MODEL"] = "glm-5.2";
        info.Environment["CATPAW_TENANT"] = settings.Tenant;
        info.Environment["CATPAW_ENCRYPT"] = "1";
        info.Environment["CATPAW_FORCE_STREAM"] = "1";
        info.Environment["CATPAW_NATIVE_AGENT"] = "1";
        info.Environment["CATPAW_MODEL_TYPE"] = "2";
        info.Environment["CATPAW_DEBUG"] = "0";
        info.Environment["CATPAW_HEADERS"] = "{\"ide-type\":\"CatPaw IDE\",\"client-type\":\"CatPaw IDE\",\"ide-version\":\"2026.2.3\",\"plugin-id\":\"mt-idekit.mt-idekit-code\",\"plugin-version\":\"2026.2.2\",\"client-env\":\"LOCAL_IDE\",\"platform-info\":\"win32-x64\",\"UI-Version\":\"0.2.2\"}";

        foreach (var key in new[] { "CATPAW_AUTH_TOKEN", "CATPAW_COOKIE", "CATPAW_USER_MIS_ID",
                     "CATPAW_AUTO_REFRESH_TOKEN", "CATPAW_CREDENTIAL_PIPE", "CATPAW_CREDENTIAL_NONCE" })
        {
            info.Environment.Remove(key);
        }

        if (settings.AuthenticationMode == AuthenticationMode.Headless)
        {
            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(pipeNonce))
                throw new InvalidOperationException("Headless credential service is not ready.");
            info.Environment["CATPAW_CREDENTIAL_PIPE"] = pipeName;
            info.Environment["CATPAW_CREDENTIAL_NONCE"] = pipeNonce;
            return;
        }

        info.Environment["CATPAW_AUTH_TOKEN"] = settings.Token;
        info.Environment["CATPAW_COOKIE"] = $"1d47d6ff96_passportid={settings.Token}; f32a546874_ssoid={settings.Token}";
        info.Environment["CATPAW_AUTO_REFRESH_TOKEN"] = settings.AuthenticationMode == AuthenticationMode.FollowDesktop ? "1" : "0";
        if (!string.IsNullOrWhiteSpace(userMis)) info.Environment["CATPAW_USER_MIS_ID"] = userMis;
    }
}
