namespace DevStudio.Application.Abstractions;

public sealed record CallbackResult(bool Succeeded, string Detail);

/// <summary>
/// Hands an OAuth callback to a CLI's own loopback listener inside the container.
/// <para>
/// The Codex browser flow bakes <c>http://localhost:1455/auth/callback</c> into its authorise
/// request, and nothing can change that from out here. If the browser is not on the container host
/// that redirect goes nowhere — so the orchestrator accepts the callback on its own port and replays
/// it to the listener locally.
/// </para>
/// </summary>
public interface ILoopbackCallbackForwarder
{
    /// <summary>
    /// Replays a callback the browser could not deliver. Only the path and query of
    /// <paramref name="callbackUrl"/> are used; the destination is always loopback on the configured
    /// port, so a pasted URL cannot redirect this anywhere else.
    /// </summary>
    Task<CallbackResult> ForwardAsync(string callbackUrl, CancellationToken ct = default);

    /// <summary>Replays a path and query taken from an inbound request to this app.</summary>
    Task<CallbackResult> ForwardAsync(string path, string queryString, CancellationToken ct = default);

    /// <summary>Port the CLI listens on for its callback.</summary>
    int CallbackPort { get; }
}
