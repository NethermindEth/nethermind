// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.Proofs;

internal static class BeaconApiRetry
{
    internal static async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxRetries - 1)
            {
                attempt++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }
    }

    internal static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;
}
