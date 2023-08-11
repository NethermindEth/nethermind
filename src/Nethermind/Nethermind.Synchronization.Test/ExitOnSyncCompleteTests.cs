// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class ExitOnSyncCompleteTests
{

    [Test]
    public async Task TestWillExit()
    {
        ISyncModeSelector syncMode = Substitute.For<ISyncModeSelector>();
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        TimeSpan exitConditionDuration = TimeSpan.FromMilliseconds(10);

        ExitOnSyncComplete.WatchForExit(syncMode, exitSource, LimboLogs.Instance, exitConditionDuration: exitConditionDuration);

        syncMode.Changed += Raise.EventWith(this, new SyncModeChangedEventArgs(SyncMode.All, SyncMode.WaitingForBlock));
        await Task.Delay(exitConditionDuration);
        syncMode.Changed += Raise.EventWith(this, new SyncModeChangedEventArgs(SyncMode.All, SyncMode.WaitingForBlock));

        exitSource.Received().Exit(0);
    }

    [Test]
    public async Task TestWillNotExitIfStillSyncing()
    {
        ISyncModeSelector syncMode = Substitute.For<ISyncModeSelector>();
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        TimeSpan exitConditionDuration = TimeSpan.FromMilliseconds(10);

        ExitOnSyncComplete.WatchForExit(syncMode, exitSource, LimboLogs.Instance, exitConditionDuration: exitConditionDuration);

        syncMode.Changed += Raise.EventWith(this, new SyncModeChangedEventArgs(SyncMode.All, SyncMode.WaitingForBlock));
        await Task.Delay(exitConditionDuration);
        syncMode.Changed += Raise.EventWith(this, new SyncModeChangedEventArgs(SyncMode.All, SyncMode.FastBlocks));

        exitSource.DidNotReceive().Exit(0);
    }
}
