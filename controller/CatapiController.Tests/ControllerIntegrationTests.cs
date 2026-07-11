using System.Diagnostics;
using CatapiController;

namespace CatapiController.Tests;

internal static class ControllerIntegrationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("headless launch uses only the credential pipe", HeadlessLaunchUsesCredentialPipeAsync);
        yield return ("follow desktop launch retains legacy credentials", FollowDesktopLaunchRetainsLegacyCredentialsAsync);
        yield return ("manual launch retains the saved token", ManualLaunchRetainsSavedTokenAsync);
        yield return ("auth presentation redacts account identifiers", AuthPresentationRedactsIdentifiersAsync);
        yield return ("activity presentation redacts credential values", ActivityPresentationRedactsCredentialsAsync);
    }

    private static Task HeadlessLaunchUsesCredentialPipeAsync()
    {
        var info = StartInfoWithInheritedCredentials();
        var settings = new ControllerSettings(
            "desktop-secret",
            "tenant-1",
            "gateway",
            AuthenticationMode.Headless);

        GatewayLaunchEnvironment.Apply(info, settings, "desktop-user", "pipe-name", "pipe-nonce");

        Equal("pipe-name", info.Environment["CATPAW_CREDENTIAL_PIPE"], "pipe should be passed");
        Equal("pipe-nonce", info.Environment["CATPAW_CREDENTIAL_NONCE"], "nonce should be passed");
        Equal("tenant-1", info.Environment["CATPAW_TENANT"], "tenant should be passed");
        Equal("glm-5.2", info.Environment["CATPAW_MODEL"], "model should be passed");
        Missing(info, "CATPAW_AUTH_TOKEN");
        Missing(info, "CATPAW_COOKIE");
        Missing(info, "CATPAW_AUTO_REFRESH_TOKEN");
        Missing(info, "CATPAW_USER_MIS_ID");
        return Task.CompletedTask;
    }

    private static Task FollowDesktopLaunchRetainsLegacyCredentialsAsync()
    {
        var info = new ProcessStartInfo();
        var settings = new ControllerSettings(
            "desktop-token",
            "tenant-2",
            "gateway",
            AuthenticationMode.FollowDesktop);

        GatewayLaunchEnvironment.Apply(info, settings, "desktop-user", null, null);

        Equal("desktop-token", info.Environment["CATPAW_AUTH_TOKEN"], "desktop token should be passed");
        Equal("desktop-user", info.Environment["CATPAW_USER_MIS_ID"], "desktop user should be passed");
        Equal("1", info.Environment["CATPAW_AUTO_REFRESH_TOKEN"], "desktop refresh should remain enabled");
        Missing(info, "CATPAW_CREDENTIAL_PIPE");
        Missing(info, "CATPAW_CREDENTIAL_NONCE");
        return Task.CompletedTask;
    }

    private static Task ManualLaunchRetainsSavedTokenAsync()
    {
        var info = new ProcessStartInfo();
        var settings = new ControllerSettings(
            "manual-token",
            "tenant-3",
            "gateway",
            AuthenticationMode.Manual);

        GatewayLaunchEnvironment.Apply(info, settings, string.Empty, null, null);

        Equal("manual-token", info.Environment["CATPAW_AUTH_TOKEN"], "manual token should be passed");
        Equal("0", info.Environment["CATPAW_AUTO_REFRESH_TOKEN"], "desktop refresh should remain disabled");
        Missing(info, "CATPAW_CREDENTIAL_PIPE");
        return Task.CompletedTask;
    }

    private static Task AuthPresentationRedactsIdentifiersAsync()
    {
        Equal("Signed in - 13******00", ControllerPresentation.FormatAuthStatus(
            new AuthStatus(true, "13812340000", "SignedIn")), "mobile should be redacted");
        Equal("Signed in - a***@example.com", ControllerPresentation.FormatAuthStatus(
            new AuthStatus(true, "alice@example.com", "SignedIn")), "email should be redacted");
        Equal("Login required", ControllerPresentation.FormatAuthStatus(
            new AuthStatus(false, "secret-account", "LoginRequired")), "signed-out identity should be omitted");
        return Task.CompletedTask;
    }

    private static Task ActivityPresentationRedactsCredentialsAsync()
    {
        var message = ControllerPresentation.RedactActivity(
            "CATPAW_AUTH_TOKEN=secret-token CATPAW_COOKIE=secret-cookie Authorization: Bearer secret-bearer");

        True(!message.Contains("secret-token", StringComparison.Ordinal), "token should be removed");
        True(!message.Contains("secret-cookie", StringComparison.Ordinal), "cookie should be removed");
        True(!message.Contains("secret-bearer", StringComparison.Ordinal), "bearer credential should be removed");
        return Task.CompletedTask;
    }

    private static ProcessStartInfo StartInfoWithInheritedCredentials()
    {
        var info = new ProcessStartInfo();
        info.Environment["CATPAW_AUTH_TOKEN"] = "inherited-token";
        info.Environment["CATPAW_COOKIE"] = "inherited-cookie";
        info.Environment["CATPAW_AUTO_REFRESH_TOKEN"] = "1";
        info.Environment["CATPAW_USER_MIS_ID"] = "inherited-user";
        return info;
    }

    private static void Missing(ProcessStartInfo info, string name)
    {
        True(!info.Environment.ContainsKey(name), $"{name} should be absent");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
        }
    }

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }
}
