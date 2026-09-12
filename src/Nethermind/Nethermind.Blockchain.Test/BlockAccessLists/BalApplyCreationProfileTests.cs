// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Autofac;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.BlockAccessLists;

/// <summary>Measurement instrument for the bulk applier's account-creation path — the shape the
/// 2026-08 state-actor A/B measured at -21% against the journaled replay. Not a correctness test.</summary>
[TestFixture]
[NonParallelizable]
[Explicit("measurement instrument, wall-clock output")]
public class BalApplyCreationProfileTests
{
    private const int SeededAccounts = 100_000;
    private const int CreatedAccounts = 1_000;
    private const int Rounds = 15;

    /// <summary>Counts scope-level account reads via the operation logger's trace lines, which are
    /// deterministic where wall time on a MemDb is not — on the rig each of these is a potential cold
    /// backend read.</summary>
    private sealed class CountingLogger : InterfaceLogger
    {
        public long AccountReads;
        public bool IsInfo => false;
        public bool IsWarn => false;
        public bool IsDebug => false;
        public bool IsTrace => true;
        public bool IsError => false;
        public void Info(string text) { }
        public void Warn(string text) { }
        public void Debug(string text) { }
        public void Error(string text, Exception? ex = null) { }
        public void Trace(string text)
        {
            if (text.Contains(": Get account")) Interlocked.Increment(ref AccountReads);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Measure_creation_heavy_apply(bool bulk)
    {
        (IWorldStateScopeProvider scopeProvider, IContainer container) = TestWorldStateFactory.CreateFlatScopeProvider();
        using IContainer _ = container;
        CountingLogger counter = new();
        scopeProvider = new WorldStateScopeOperationLogger(scopeProvider, new OneLoggerLogManager(new ILogger(counter)));
        WorldState worldState = new(scopeProvider, LimboLogs.Instance);

        Hash256 parentRoot;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            byte[] balance = new byte[20];
            for (int i = 0; i < SeededAccounts; i++)
            {
                Keccak.Compute("seed" + i).Bytes[..20].CopyTo(balance);
                worldState.CreateAccount(new Address((byte[])balance.Clone()), (UInt256)(i + 1));
            }

            worldState.Commit(Amsterdam.Instance, isGenesis: true);
            worldState.CommitTree(0);
            parentRoot = worldState.StateRoot;
        }

        ReadOnlyBlockAccessList bal = BuildCreationBal();
        BlockHeader parentHeader = Build.A.BlockHeader.WithNumber(0).WithStateRoot(parentRoot).TestObject;

        using BlockAccessListManager manager = new(
            worldState,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true, ParallelBalBulkApply = bulk },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(
                Substitute.For<IBlockhashProvider>(),
                new TestSingleReleaseSpecProvider(Amsterdam.Instance),
                LimboLogs.Instance,
                static ws => new EthereumCodeInfoRepository(ws)));

        // Warmup round outside the timing, then timed rounds on fresh scopes over the same parent.
        for (int round = -1; round < Rounds; round++)
        {
            using IDisposable scope = worldState.BeginScope(parentHeader);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            long readsBefore = Interlocked.Read(ref counter.AccountReads);
            Stopwatch sw = Stopwatch.StartNew();
            manager.ApplyBlockStateChanges(bal, worldState, Amsterdam.Instance, shouldComputeStateRoot: true);
            sw.Stop();
            long reads = Interlocked.Read(ref counter.AccountReads) - readsBefore;
            if (round >= 0)
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bal-apply-profile.txt"),
                    $"APPLY {(bulk ? "bulk" : "journal")} round {round}: {sw.Elapsed.TotalMilliseconds:F1} ms accountReads={reads} root={worldState.StateRoot}{Environment.NewLine}");
            }

            worldState.Reset();
        }
    }

    private static ReadOnlyBlockAccessList BuildCreationBal()
    {
        byte[] code = new byte[100];
        code[0] = 0x60;

        ReadOnlyAccountChanges[] accounts = new ReadOnlyAccountChanges[CreatedAccounts];
        for (int i = 0; i < CreatedAccounts; i++)
        {
            accounts[i] = Build.An.AccountChanges
                .WithAddress(new Address(Keccak.Compute("created" + i)))
                .WithBalanceChanges(new BalanceChange(1, (UInt256)(i + 1)))
                .WithNonceChanges(new NonceChange(1, 1))
                .WithCodeChanges(new CodeChange(1, code))
                .TestObject;
        }

        return Build.A.BlockAccessList.WithAccountChanges(accounts).TestObject;
    }
}
