// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Test;

/// <summary>
/// Direct state-level probes for the same-block SELFDESTRUCT + recreate pattern that
/// failed on Sepolia block 4913057. These tests bypass block production entirely and
/// exercise <see cref="WorldState"/> with <see cref="PrewarmerScopeProvider"/> wired
/// the same way the main processing path wires it (cache-populating prewarmer scope
/// + cache-consuming "main" scope sharing one <see cref="PreBlockCaches"/>).
///
/// They probe:
///  1. After the prewarmer warms <c>preBlockCaches.StateCache[X] = parentAccount</c>,
///     does the main scope's <c>IsContract(X)</c>/<c>IsNonZeroAccount(X)</c> return
///     the post-destroy state (false) rather than the cached parent state?
///  2. Same after a value transfer recreates the account between destroy and read,
///     mirroring tx 0x39 → tx 0x3a in the failing block.
/// </summary>
[Parallelizable(ParallelScope.None)]
public class SelfDestructPrewarmerStateTests
{
    private static readonly Address X = TestItem.AddressA;
    private static readonly byte[] ContractCode = [0x33, 0xff];

    private static (WorldState mainState, WorldState prewarmerState, PreBlockCaches caches, BlockHeader genesis)
        BuildSharedCacheWorldStates()
    {
        // One backing TrieStore + one in-memory state DB shared by both the main and
        // prewarmer scopes (they each take their own resettable scope on top of it).
        IDbProvider dbProvider = TestMemDbProvider.Init();
        PruningConfig pruningConfig = new();
        TestFinalizedStateProvider finalized = new(pruningConfig.PruningBoundary);
        TrieStore trieStore = new(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalized,
            pruningConfig,
            LimboLogs.Instance);
        finalized.TrieStore = trieStore;

        TrieStoreScopeProvider baseProvider = new(trieStore, dbProvider.CodeDb, LimboLogs.Instance);
        PreBlockCaches caches = new();

        // Pre-genesis: place the contract X with code and seed slot 7 = 7 (so we can
        // observe storage clearing too).
        WorldState seedState = new(baseProvider, LimboLogs.Instance);
        Hash256 seedStateRoot;
        using (seedState.BeginScope(IWorldState.PreGenesis))
        {
            seedState.CreateAccount(X, Unit.Ether);
            seedState.InsertCode(X, ContractCode, Shanghai.Instance);
            seedState.Set(new StorageCell(X, 7), new byte[] { 7 });
            seedState.Commit(Shanghai.Instance);
            seedState.CommitTree(0);
            seedStateRoot = seedState.StateRoot;
        }

        BlockHeader genesis = Build.A.BlockHeader.WithStateRoot(seedStateRoot).WithNumber(0).TestObject;

        WorldState mainState = new(
            new PrewarmerScopeProvider(baseProvider, caches, populatePreBlockCache: false),
            LimboLogs.Instance);

        WorldState prewarmerState = new(
            new PrewarmerScopeProvider(baseProvider, caches, populatePreBlockCache: true),
            LimboLogs.Instance);

        return (mainState, prewarmerState, caches, genesis);
    }

    [Test]
    public void Main_scope_sees_destroyed_account_even_with_prewarmed_cache()
    {
        (WorldState mainState, WorldState prewarmerState, PreBlockCaches caches, BlockHeader genesis) = BuildSharedCacheWorldStates();

        // Step 1: prewarmer reads X — populates caches.StateCache with parentAccount (with code).
        using (prewarmerState.BeginScope(genesis))
        {
            prewarmerState.IsContract(X).Should().BeTrue("contract has code at parent state");
            prewarmerState.GetCode(X).Should().Equal(ContractCode);
        }
        caches.StateCache.TryGetValue((AddressAsKey)X, out Account? cached).Should().BeTrue("prewarmer should have populated state cache");
        cached!.HasCode.Should().BeTrue("cached parent account must have code");

        // Step 2: main scope simulates tx0 (destroy) and then reads.
        using (mainState.BeginScope(genesis))
        {
            // tx0: destroy
            mainState.ClearStorage(X);
            mainState.DeleteAccount(X);
            mainState.Commit(Shanghai.Instance, NullStateTracer.Instance, isGenesis: false, commitRoots: false);

            // tx1 begins: collision-relevant probes.
            mainState.AccountExists(X).Should().BeFalse("X must not exist after destroy + commit");
            mainState.IsContract(X).Should().BeFalse("X must not be a contract after destroy + commit");

            // EIP-684/7610 collision check uses IsNonZeroAccount (in WorldState):
            bool nonZero = mainState.IsNonZeroAccount(X, out bool exists);
            exists.Should().BeFalse("AccountExists must be false");
            nonZero.Should().BeFalse("IsNonZeroAccount must be false → CREATE may proceed");
        }
    }

    /// <summary>
    /// Regression for the actual Sepolia 4913057 fault. <see cref="IWorldState.IsNonZeroAccount"/>
    /// is a default interface method that, on a class that does not provide its own
    /// <c>IAccountStateProvider.IsStorageEmpty</c>/<c>TryGetAccount</c> (e.g. <c>ParallelWorldState</c>
    /// wrapping <c>WorldState</c>), routes through the default <c>IAccountStateProvider.IsStorageEmpty</c>
    /// which calls <c>TryGetAccount</c>. <see cref="WorldState.IAccountStateProvider.TryGetAccount"/>
    /// builds the <see cref="AccountStruct"/> by calling <c>_persistentStorageProvider.GetStorageRoot</c>,
    /// and that method does NOT honour the same-block SELFDESTRUCT marker
    /// (<c>PerContractState.BlockChange.HasClear</c>), unlike <c>PerContractState.IsEmpty</c>.
    /// As a result, after a same-block destroy + recreate via value transfer the AccountStruct
    /// carries the destroyed contract's parent <c>StorageRoot</c>, <c>IsStorageEmpty</c> returns
    /// false, and a CREATE collides where it should not.
    /// </summary>
    [Test]
    public void IsNonZeroAccount_via_IWorldState_default_returns_false_after_destroy_and_value_transfer()
    {
        // Use ParallelWorldState (production decorator over WorldState) so we exercise the
        // default-interface-method dispatch path that the EVM actually hits in production.
        IDbProvider dbProvider = TestMemDbProvider.Init();
        PruningConfig pruningConfig = new();
        TestFinalizedStateProvider finalized = new(pruningConfig.PruningBoundary);
        TrieStore trieStore = new(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalized,
            pruningConfig,
            LimboLogs.Instance);
        finalized.TrieStore = trieStore;

        TrieStoreScopeProvider baseProvider = new(trieStore, dbProvider.CodeDb, LimboLogs.Instance);
        WorldState inner = new(baseProvider, LimboLogs.Instance);
        ParallelWorldState worldState = new(inner);

        // Pre-genesis: contract X with code AND non-empty storage (so its StorageRoot is non-empty).
        Hash256 seedRoot;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(X, Unit.Ether);
            worldState.InsertCode(X, ContractCode, Shanghai.Instance);
            worldState.Set(new StorageCell(X, 7), new byte[] { 7 });
            worldState.Commit(Shanghai.Instance);
            worldState.CommitTree(0);
            seedRoot = worldState.StateRoot;
        }

        BlockHeader genesis = Build.A.BlockHeader.WithStateRoot(seedRoot).WithNumber(0).TestObject;

        using (worldState.BeginScope(genesis))
        {
            // Force the read that primes TrieStoreWorldStateBackendScope._loadedAccounts with
            // the parent (with non-empty StorageRoot). The EVM's first interaction with X in
            // tx 0x2d (the SELFDESTRUCT call) does this implicitly when it loads X's code.
            worldState.GetCodeHash(X);

            // tx 0x2d: SELFDESTRUCT.
            worldState.ClearStorage(X);
            worldState.DeleteAccount(X);
            worldState.Commit(Shanghai.Instance, NullStateTracer.Instance, isGenesis: false, commitRoots: false);

            // tx 0x39: value transfer recreates the empty account at X.
            worldState.AddToBalanceAndCreateIfNotExists(X, UInt256.One, Shanghai.Instance);
            worldState.Commit(Shanghai.Instance, NullStateTracer.Instance, isGenesis: false, commitRoots: false);

            // tx 0x3a's CREATE collision check, exactly as the EVM performs it
            // (IWorldState reference, default interface method).
            IWorldState asInterface = worldState;
            bool isNonZero = asInterface.IsNonZeroAccount(X, out bool exists);

            exists.Should().BeTrue("X exists after value transfer");
            isNonZero.Should().BeFalse(
                "the new account at X has no code, zero nonce and an empty storage tree (HasClear marker); " +
                "CREATE inside the metamorphic transient must therefore not collide");
        }
    }

    [Test]
    public void Main_scope_sees_recreated_account_as_collision_free_after_value_transfer()
    {
        (WorldState mainState, WorldState prewarmerState, PreBlockCaches caches, BlockHeader genesis) = BuildSharedCacheWorldStates();

        // Step 1: prewarmer pre-populates state cache with parentAccount (has code).
        using (prewarmerState.BeginScope(genesis))
        {
            prewarmerState.GetAccount(X);
            ((IAccountStateProvider)prewarmerState).IsStorageEmpty(X).Should().BeFalse("seed has slot 7 set");
        }

        // Step 2: main scope simulates tx 0x2d (destroy), tx 0x39 (value transfer),
        // then probes collision before tx 0x3a's CREATE.
        using (mainState.BeginScope(genesis))
        {
            // tx 0x2d: SELFDESTRUCT effect at end of tx.
            mainState.ClearStorage(X);
            mainState.DeleteAccount(X);
            mainState.Commit(Shanghai.Instance, NullStateTracer.Instance, isGenesis: false, commitRoots: false);

            // tx 0x39: send 1 wei to X — recreates an empty account.
            mainState.AddToBalanceAndCreateIfNotExists(X, UInt256.One, Shanghai.Instance);
            mainState.Commit(Shanghai.Instance, NullStateTracer.Instance, isGenesis: false, commitRoots: false);

            // tx 0x3a's collision check on X. The new account is a fresh
            // empty-with-balance account: no code, no nonce, empty storage.
            mainState.AccountExists(X).Should().BeTrue("X must exist after value transfer");
            mainState.IsContract(X).Should().BeFalse("X must NOT have code after value transfer; cache must not pollute");
            mainState.GetNonce(X).Should().Be((UInt256)0);
            ((IAccountStateProvider)mainState).IsStorageEmpty(X).Should().BeTrue("storage must be empty after destroy + value transfer");

            bool nonZero = mainState.IsNonZeroAccount(X, out bool exists);
            exists.Should().BeTrue();
            nonZero.Should().BeFalse(
                "this is the CREATE collision check that fails on the user's node — recreate must not look like a contract");
        }
    }
}
