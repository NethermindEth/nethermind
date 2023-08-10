// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization;

public class ExitOnSyncComplete
{
    public ExitOnSyncComplete(
        ISyncModeSelector syncMode,
        IProcessExitSource exitSource,
        ILogManager logManager
    ) {
        ILogger logger = logManager.GetClassLogger();

        DateTime lastExitConditionTime = DateTime.MaxValue;
        TimeSpan exitConditionDuration = TimeSpan.FromSeconds(5);
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
