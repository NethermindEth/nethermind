// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.BlockAccessLists;

/// <summary>
/// Covers the diagnostics-only shadow BAL state-root recomputation
/// (<c>IBlocksConfig.ParallelBalStateRootShadow</c>): the bulk post-value apply must reproduce
/// the canonical <c>ApplyStateChanges</c> root, and failures must never escape into the pipeline.
/// </summary>
public class BalShadowStateRootTests
{
    private static readonly Address ContractAddress = TestItem.AddressB;
    private static readonly byte[] ContractCode = [0x60, 0x2A, 0x60, 0x00, 0x55];

    [Test]
    public void Shadow_root_matches_canonical_for_created_account()
        => AssertShadowMatchesCanonical(
            genesisSetup: null,
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, 25))
                    .WithNonceChanges(new NonceChange(1, 1))
                    .TestObject)
                .TestObject);

    [Test]
    public void Shadow_root_matches_canonical_for_balance_only_change()
        => AssertShadowMatchesCanonical(
            genesisSetup: static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100),
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, 150))
                    .TestObject)
                .TestObject);

    [Test]
    public void Shadow_root_matches_canonical_for_storage_changes()
        => AssertShadowMatchesCanonical(
            genesisSetup: static stateProvider =>
            {
                stateProvider.CreateAccount(ContractAddress, 1, 1);
                stateProvider.InsertCode(ContractAddress, ContractCode, Amsterdam.Instance);
                stateProvider.Set(new StorageCell(ContractAddress, 1), [0x2A]);
                stateProvider.Set(new StorageCell(ContractAddress, 2), [0x0B]);
            },
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(ContractAddress)
                    .WithStorageChanges(1, new StorageChange(1, 0x99u)) // overwrite
                    .WithStorageChanges(2, new StorageChange(1, 0x00u)) // zero out => delete
                    .WithStorageChanges(3, new StorageChange(1, 0x07u)) // create
                    .TestObject)
                .TestObject);

    [Test]
    public void Shadow_root_matches_canonical_for_code_deploy()
        => AssertShadowMatchesCanonical(
            genesisSetup: null,
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(ContractAddress)
                    .WithNonceChanges(new NonceChange(1, 1))
                    .WithCodeChanges(new CodeChange(1, ContractCode))
                    .WithStorageChanges(1, new StorageChange(1, 0x2Au))
                    .TestObject)
                .TestObject);

    [Test]
    public void Shadow_root_matches_canonical_for_eip158_swept_account()
        => AssertShadowMatchesCanonical(
            // Zeroing the balance leaves a touched, totally empty account: canonical relies on
            // the EIP-158 commit sweep, the shadow on BalPostState returning null.
            genesisSetup: static stateProvider =>
            {
                stateProvider.CreateAccount(TestItem.AddressA, 100);
                stateProvider.CreateAccount(TestItem.AddressC, 7); // survives, so the root is not the empty root
            },
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, UInt256.Zero))
                    .TestObject)
                .TestObject);

    [Test]
    public void Shadow_reports_mismatch_without_throwing()
    {
        ShadowRunResult result = RunShadowScenario(
            SimpleTransferBal(),
            genesisSetup: static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100),
            canonicalRootOverride: TestItem.KeccakF);

        Assert.That(result.MismatchDelta, Is.EqualTo(1));
    }

    [Test]
    public void Shadow_is_inert_when_flag_is_off()
    {
        CountingEnvFactory countingFactory = new();
        ShadowRunResult result = RunShadowScenario(
            SimpleTransferBal(),
            genesisSetup: static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100),
            shadowEnabled: false,
            shadowEnvFactory: countingFactory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MismatchDelta, Is.Zero);
            Assert.That(result.Errors, Is.Empty);
            Assert.That(countingFactory.CreateCalls, Is.Zero, "the shadow env must not even be created when the flag is off");
        }
    }

    [Test]
    public void Shadow_swallows_env_failures()
    {
        ShadowRunResult result = RunShadowScenario(
            SimpleTransferBal(),
            genesisSetup: static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100),
            shadowEnvFactory: new ThrowingEnvFactory());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MismatchDelta, Is.Zero);
            Assert.That(result.Errors, Has.One.Contains("shadow state root computation failed"));
        }
    }

    [Test]
    public void Shadow_does_not_throw_for_corrupt_bal()
    {
        // Storage changes on an account that never materializes (no parent, no field changes):
        // canonical processing rejects such a block elsewhere; the shadow must stay silent apart
        // from a mismatch report.
        ReadOnlyBlockAccessList corrupt = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressD)
                .WithStorageChanges(1, new StorageChange(1, 0x99u))
                .TestObject)
            .TestObject;

        ShadowRunResult result = RunShadowScenario(
            corrupt,
            genesisSetup: static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100),
            canonicalRootOverride: TestItem.KeccakF,
            applyCanonical: false);

        Assert.That(result.MismatchDelta, Is.EqualTo(1), "the corrupt BAL must surface as a mismatch report, not a crash");
    }

    private sealed record ShadowRunResult(long MismatchDelta, List<string> Errors);

    /// <summary>Captures error log lines including exception details, so scenario asserts can
    /// prove the shadow actually ran instead of silently swallowing a failure.</summary>
    private sealed class CapturingLogger : InterfaceLogger
    {
        public List<string> Errors { get; } = [];

        public void Info(string text) { }
        public void Warn(string text) { }
        public void Debug(string text) { }
        public void Trace(string text) { }
        public void Error(string text, Exception? ex = null) => Errors.Add(ex is null ? text : $"{text} {ex}");

        public bool IsInfo => false;
        public bool IsWarn => false;
        public bool IsDebug => false;
        public bool IsTrace => false;
        public bool IsError => true;
    }

    private static ReadOnlyBlockAccessList SimpleTransferBal() =>
        Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(1, 60))
                .WithNonceChanges(new NonceChange(1, 1))
                .TestObject)
            .TestObject;

    private static void AssertShadowMatchesCanonical(Action<IWorldState>? genesisSetup, ReadOnlyBlockAccessList bal)
    {
        ShadowRunResult result = RunShadowScenario(bal, genesisSetup);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Errors, Is.Empty, "the shadow run must complete without errors");
            Assert.That(result.MismatchDelta, Is.Zero, "shadow root must match the canonical BAL-applied root");
        }
    }

    /// <summary>
    /// Applies <paramref name="bal"/> canonically (via <see cref="BlockAccessListManager.ApplyStateChanges"/>)
    /// on a real world state at a genesis parent, then runs the shadow comparison and returns the
    /// change in <see cref="Evm.Metrics.BalShadowRootMismatches"/>.
    /// </summary>
    private static ShadowRunResult RunShadowScenario(
        ReadOnlyBlockAccessList bal,
        Action<IWorldState>? genesisSetup,
        bool shadowEnabled = true,
        Hash256? canonicalRootOverride = null,
        IReadOnlyTxProcessingEnvFactory? shadowEnvFactory = null,
        bool applyCanonical = true)
    {
        CapturingLogger capturingLogger = new();
        // The main world state and the shadow env share one TrieStore, mirroring production where
        // read-only envs read through the same store as the main processing world state.
        (TrieStore trieStore, IDb codeDb) = CreateSharedStore();
        IWorldState stateProvider = new WorldState(new TrieStoreScopeProvider(trieStore, codeDb, LimboLogs.Instance), LimboLogs.Instance);
        Hash256 parentStateRoot;
        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            genesisSetup?.Invoke(stateProvider);
            stateProvider.Commit(Amsterdam.Instance, isGenesis: true);
            stateProvider.CommitTree(0);
            parentStateRoot = stateProvider.StateRoot;
        }

        Hash256 parentHash = TestItem.KeccakA;
        BlockHeader parentHeader = Build.A.BlockHeader
            .WithNumber(0)
            .WithHash(parentHash)
            .WithStateRoot(parentStateRoot)
            .TestObject;

        using IDisposable parentScope = stateProvider.BeginScope(parentHeader);
        using BlockAccessListManager balManager = new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            new OneLoggerLogManager(new ILogger(capturingLogger)),
            new BlocksConfig { ParallelExecution = true, ParallelBalStateRootShadow = shadowEnabled },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            static worldState => new EthereumCodeInfoRepository(worldState),
            readOnlyTxProcessingEnvFactory: shadowEnvFactory ?? new SharedStoreEnvFactory(trieStore, codeDb));

        Block block = Build.A.Block
            .WithNumber(1)
            .WithParentHash(parentHash)
            .WithBlockAccessList(bal)
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        Assert.That(balManager.ParallelExecutionEnabled, Is.True);

        if (applyCanonical)
        {
            // The same state transition the parallel path performs in iteration 0.
            BlockAccessListManager.ApplyStateChanges(bal, stateProvider, Amsterdam.Instance, shouldComputeStateRoot: true);
        }
        block.Header.StateRoot = canonicalRootOverride ?? stateProvider.StateRoot;

        long mismatchesBefore = Evm.Metrics.BalShadowRootMismatches;
        Assert.DoesNotThrow(() => balManager.RunShadowStateRootComparison(block));
        return new ShadowRunResult(Evm.Metrics.BalShadowRootMismatches - mismatchesBefore, capturingLogger.Errors);
    }

    private static (TrieStore trieStore, IDb codeDb) CreateSharedStore()
    {
        PruningConfig pruningConfig = new();
        TestFinalizedStateProvider finalizedStateProvider = new(pruningConfig.PruningBoundary);
        IDbProvider dbProvider = TestMemDbProvider.Init();
        TrieStore trieStore = new(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalizedStateProvider,
            pruningConfig,
            LimboLogs.Instance);
        finalizedStateProvider.TrieStore = trieStore;
        return (trieStore, dbProvider.CodeDb);
    }

    /// <summary>
    /// Real read-only env over the same trie store as the main world state: each Build opens
    /// an independent <see cref="IWorldState"/> scope at the requested root.
    /// </summary>
    private sealed class SharedStoreEnvFactory(TrieStore trieStore, IDb codeDb) : IReadOnlyTxProcessingEnvFactory
    {
        public IReadOnlyTxProcessorSource Create() => new Source(trieStore, codeDb);

        private sealed class Source(TrieStore trieStore, IDb codeDb) : IReadOnlyTxProcessorSource
        {
            public IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock)
            {
                IWorldState worldState = new WorldState(new TrieStoreScopeProvider(trieStore, codeDb, LimboLogs.Instance), LimboLogs.Instance);
                return new Scope(worldState, worldState.BeginScope(baseBlock));
            }

            public void Dispose() { }
        }

        private sealed class Scope(IWorldState worldState, IDisposable stateScope) : IReadOnlyTxProcessingScope
        {
            public ITransactionProcessor TransactionProcessor => throw new NotSupportedException();
            public IWorldState WorldState => worldState;
            public void Dispose() => stateScope.Dispose();
        }
    }

    private sealed class CountingEnvFactory : IReadOnlyTxProcessingEnvFactory
    {
        public int CreateCalls { get; private set; }

        public IReadOnlyTxProcessorSource Create()
        {
            CreateCalls++;
            return Substitute.For<IReadOnlyTxProcessorSource>();
        }
    }

    private sealed class ThrowingEnvFactory : IReadOnlyTxProcessingEnvFactory
    {
        public IReadOnlyTxProcessorSource Create() => throw new InvalidOperationException("shadow env unavailable");
    }
}
