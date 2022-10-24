using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Timers;

public static class FunctionalTimer
{
    /// <summary>
    /// Simple util to periodically run something. Interval does not include the time taken by the action itself, so
    /// not accurate. Blocks until cancellation token is cancelled, swallows `TaskCancelledException` (even by action)
    /// by design.
    /// </summary>
    /// <param name="interval"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="action"></param>
    public static async Task RunEvery(TimeSpan interval, CancellationToken cancellationToken, Action<CancellationToken> action)
    {
        do
        {
            try
            {
                action(cancellationToken);

                await Task.Delay(interval, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        } while (!cancellationToken.IsCancellationRequested);
    }
}
