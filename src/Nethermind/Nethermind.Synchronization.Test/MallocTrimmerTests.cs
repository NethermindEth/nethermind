// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class MallocTrimmerTests
{
    [TestCase(SyncMode.None, false)]
    [TestCase(SyncMode.WaitingForBlock, false)]
    [TestCase(SyncMode.FastBlocks, true)]
    public async Task TestTrim(SyncMode mode, bool doReceive)
    {
        MallocHelper helper = Substitute.For<MallocHelper>();

        ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
        new MallocTrimmer(syncModeSelector, TimeSpan.FromMilliseconds(1), NullLogManager.Instance, helper);

        syncModeSelector.Changed += Raise.EventWith(null,
            new SyncModeChangedEventArgs(SyncMode.FastSync, mode));

        await Task.Delay(TimeSpan.FromMilliseconds(100));

        syncModeSelector.Changed += Raise.EventWith(null,
            new SyncModeChangedEventArgs(SyncMode.None, SyncMode.None));

        if (doReceive)
        {
            helper.Received().MallocTrim(Arg.Any<uint>());
        }
        else
        {
            helper.DidNotReceive().MallocTrim(Arg.Any<uint>());
        }
    }
}
