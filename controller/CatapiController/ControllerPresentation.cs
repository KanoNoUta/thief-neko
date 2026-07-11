using System.Text.RegularExpressions;

namespace CatapiController;

internal static partial class ControllerPresentation
{
    internal static string FormatAuthStatus(AuthStatus status)
    {
        if (!status.SignedIn) return status.State == "RefreshPending" ? "Refresh pending" : "Login required";
        return $"Signed in - {RedactAccount(status.AccountLabel)}";
    }

    internal static string RedactActivity(string message) => CredentialPattern().Replace(message, "$1[redacted]");

    private static string RedactAccount(string value)
    {
        if (Regex.IsMatch(value, @"^1\d{10}$")) return value[..2] + "******" + value[^2..];
        var at = value.IndexOf('@');
        if (at > 0) return value[..1] + "***" + value[at..];
        return value.Length <= 2 ? "***" : value[..1] + "***" + value[^1..];
    }

    [GeneratedRegex(@"(?i)(CATPAW_AUTH_TOKEN\s*=\s*|CATPAW_COOKIE\s*=\s*|Authorization\s*:\s*Bearer\s+)([^\s]+)")]
    private static partial Regex CredentialPattern();
}
