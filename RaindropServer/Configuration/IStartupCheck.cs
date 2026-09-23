namespace RaindropServer.Configuration;

/// <summary>
/// A check that can only be made by going and looking: the media server actually answers, the
/// credentials it was given are actually the ones it accepts, the host name actually resolves.
/// </summary>
/// <remarks>
/// Settings that can be judged by reading them are judged by the component's own
/// <c>Validate(ConfigurationReport)</c> before anything is registered. This is the other half — run
/// once, after registration and before the server listens, so that an owner learns their voice calls
/// will not work from a line on startup rather than from the first member who tries to join.
/// <para>
/// The seam a plugin adds its own to: register one with
/// <c>AddStartupCheck&lt;T&gt;()</c> and it is run beside the built-in ones. A check reports what it
/// finds rather than throwing, and decides for itself which of its findings are bad enough to stop
/// the server — being unable to reach a sidecar that has not finished starting usually is not.
/// </para>
/// </remarks>
public interface IStartupCheck
{
    /// <summary>
    /// What this check is called while it runs, e.g. <c>voice (livekit)</c>. Shown to whoever is
    /// watching the server start, so it reads as a thing being checked rather than a class name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Goes and looks, recording anything wrong in <paramref name="report"/>. Should not throw:
    /// whatever goes wrong is itself a finding, and an exception is recorded as an error against
    /// <see cref="Name"/> by the caller.
    /// </summary>
    Task CheckAsync(ConfigurationReport report, CancellationToken cancellationToken);
}
