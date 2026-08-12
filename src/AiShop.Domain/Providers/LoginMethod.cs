namespace AiShop.Domain.Providers;

/// <summary>
/// How a login is completed. Which of these a CLI offers differs, so each adapter declares its own
/// supported set — there is no single flow that suits every provider or every network.
/// </summary>
public enum LoginMethod
{
    /// <summary>
    /// The CLI's normal sign-in: it prints a link, and either catches the redirect on a local port
    /// or asks for a code to be pasted back. Needs the callback port published for Codex.
    /// </summary>
    Browser = 0,

    /// <summary>
    /// Device-code flow: a link plus a short code typed into the browser. Nothing has to reach back
    /// into the container, so it works from anywhere.
    /// </summary>
    DeviceCode = 1,

    /// <summary>
    /// A key or token pasted straight in. The CLI stores it in its own credential store — the
    /// orchestrator never persists the secret itself.
    /// </summary>
    Token = 2,

    /// <summary>Sign in with an API-billed Console account instead of a subscription.</summary>
    Console = 3,

    /// <summary>
    /// Generate a long-lived credential through the browser, for unattended work. Nothing is pasted
    /// in — the CLI mints and stores it.
    /// </summary>
    LongLivedToken = 4,
}

public static class LoginMethodExtensions
{
    public static string Describe(this LoginMethod method) => method switch
    {
        LoginMethod.Browser => "Browser sign-in",
        LoginMethod.DeviceCode => "Device code",
        LoginMethod.Token => "Paste a token",
        LoginMethod.Console => "Console account",
        LoginMethod.LongLivedToken => "Long-lived token",
        _ => method.ToString(),
    };

    /// <summary>Whether the flow expects a secret to be typed in rather than read from a browser.</summary>
    public static bool NeedsSecret(this LoginMethod method) => method == LoginMethod.Token;
}
