// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class MallocTrimmerTests
{
    [Test]
    public async Task TestTrim()
    {
        MallocHelper helper = Substitute.For<MallocHelper>();

        MallocTrimmer trimmer = new MallocTrimmer(TimeSpan.FromMilliseconds(1), NullLogManager.Instance, helper);
        CancellationTokenSource cts = new CancellationTokenSource();
        Task trimmerTask = trimmer.Run(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        cts.Cancel();
        await trimmerTask;

        helper.Received().MallocTrim(Arg.Any<uint>());
    }
}
