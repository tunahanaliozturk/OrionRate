namespace Moongazing.OrionRate;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Checks and consumes rate-limit permits for a key against a named policy. This is the library path:
/// resolve the key yourself (see <see cref="Key"/>) and call <see cref="AcquireAsync"/>. The Wave 3
/// ASP.NET middleware layers request-driven key resolution and HTTP headers on top of this.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Attempt to acquire <paramref name="permits"/> for <paramref name="key"/> under
    /// <paramref name="policy"/>. Returns the decision — allowed or throttled — without throwing when
    /// the limit is exceeded (a rejection is data, not an exception).
    /// </summary>
    /// <param name="policy">The name of a registered policy.</param>
    /// <param name="key">The identity being limited (e.g. <c>Key.Tenant.Of("acme")</c>).</param>
    /// <param name="permits">The number of permits to acquire (usually 1).</param>
    /// <param name="cancellationToken">Cancellation token (honoured by distributed stores in later waves).</param>
    /// <returns>The decision.</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">No policy named <paramref name="policy"/> is registered.</exception>
    ValueTask<RateResult> AcquireAsync(string policy, string key, int permits = 1, CancellationToken cancellationToken = default);
}
