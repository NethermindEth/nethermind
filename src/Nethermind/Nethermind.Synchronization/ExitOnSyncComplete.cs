// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization;

public static class ExitOnSyncComplete
{
    public static void WatchForExit(
        ISyncModeSelector syncMode,
        IProcessExitSource exitSource,
        ILogManager logManager,
        TimeSpan? exitConditionDuration = null
    )
    {
        ILogger logger = logManager.GetClassLogger();

        // Usually there are time where the mode changed to WaitingForBlock temporarily. So there need to be a small
        // wait to make sure the sync really is completed.
        exitConditionDuration ??= TimeSpan.FromSeconds(5);

        DateTime lastExitConditionTime = DateTime.MaxValue;
        syncMode.Changed += ((sender, args) =>
        {
            if (args.Current is SyncMode.WaitingForBlock or SyncMode.None)
            {
                if (lastExitConditionTime == DateTime.MaxValue)
                {
                    lastExitConditionTime = DateTime.Now;
                }
                else if (DateTime.Now - lastExitConditionTime > exitConditionDuration)
                {
                    logger.Info($"Sync finished. Exiting....");
                    exitSource.Exit(0);
                }
            }
            else
            {
                lastExitConditionTime = DateTime.MaxValue;
            }
        });
    }
}
