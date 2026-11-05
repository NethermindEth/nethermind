// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.EraE;

internal static class EraJobRunner
{
    internal static async Task RunProtected(Func<CancellationToken, Task> job, string cancelledMessage, string errorMessage, ILogger logger, CancellationToken ct, Action? onFinally = null)
    {
        try
        {
            await job(ct);
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            logger.Warn(cancelledMessage);
        }
        catch (Exception e)
        {
            logger.Error(errorMessage, e);
        }
        finally
        {
            onFinally?.Invoke();
        }
    }
}
