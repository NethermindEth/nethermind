// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.Random;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

[TestFixture]
public class DetailedProgressSerializerTest
{
    private readonly DetailedProgress _data = new(1, null);

    [Test]
    public void SerializerMultiThreadFuzzTest()
    {
        CancellationTokenSource cts = new();
        Task.Run(() => ChangeData(cts.Token));

        for (int i = 0; i < 1000000; i++)
        {
            _data.Serialize();
        }

        cts.Cancel();
    }

    [Test]
    public void DisplayProgressReport_reports_heal_phase_without_full_state_estimate_when_snap_sync([Values] bool snapSync)
    {
        DetailedProgress data = new(BlockchainIds.Mainnet, null, snapSync) { DataSize = 200_000_000 };
        TestLogger testLogger = new() { IsDebug = false };
        ILogger logger = new(testLogger);

        data.DisplayProgressReport(0, new BranchProgress(0, logger), logger);

        string report = testLogger.LogList[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, snapSync ? Does.StartWith("State Sync (Phase 2 of 2, healing) ") : Does.StartWith("State Sync  "));
            Assert.That(report, snapSync ? Does.Not.Contain("%)") : Does.Contain("%)"));
        }
    }

    private void ChangeData(CancellationToken token)
    {
        Random rand = new();
        Random randBool = new();
        while (!token.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _data.ConsumedNodesCount, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.ConsumedNodesCount, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.SavedStorageCount, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.SavedStateCount, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.SavedNodesCount, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.SavedAccounts, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.SavedCode, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.RequestedNodesCount, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.DbChecks, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.StateWasThere, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.StateWasNotThere, randBool.NextBoolean() ? rand.NextInt64() : 0);
            Interlocked.Exchange(ref _data.DataSize, randBool.NextBoolean() ? rand.NextInt64() : 0);
        }
    }
}
