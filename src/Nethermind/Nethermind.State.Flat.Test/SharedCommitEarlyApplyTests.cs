// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Transactions committed together while their changes commit as they read (as block processing shares the commits of
/// prewarm takeovers) reach the state root of committing each, on the main block processing flat state that applies
/// committed storage writes to early storage trees on its idle thread.
/// </summary>
/// <remarks>Not parallelizable: the idle gap the early apply waits for is process-wide.</remarks>
[TestFixture]
[NonParallelizable]
public class SharedCommitEarlyApplyTests
{
    private static readonly IReleaseSpec Spec = Cancun.Instance;
    private static readonly Address[] Contracts = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC];
    private static readonly Address Funded = TestItem.AddressD;
    private static readonly Address Empty = TestItem.AddressE;
    private static readonly Address Missing = TestItem.AddressF;
    private static readonly byte[] Code = [0x60, 0x00, 0x60, 0x00];
    private const int Slots = 10;

    [Test]
    public void Shared_commits_with_storage_applied_early_reach_the_root_of_committing_each()
    {
        TimeSpan gap = IdleStorageApplier.MinIdleGap;
        IdleStorageApplier.MinIdleGap = TimeSpan.Zero;
        try
        {
            using Context early = new(applyEarly: true);
            using Context late = new(applyEarly: false);
            int applied = 0;
            int shared = 0;
            for (int seed = 0; seed < 150; seed++)
            {
                Hash256 reference = late.Run(new Random(seed), share: false, out _, out _);
                Hash256 each = early.Run(new Random(seed), share: false, out int appliedEach, out _);
                Hash256 together = early.Run(new Random(seed), share: true, out int appliedShared, out int sharedCommits);
                applied += appliedEach + appliedShared;
                shared += sharedCommits;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(each, Is.EqualTo(reference), $"seed {seed}: committed one by one, storage applied early");
                    Assert.That(together, Is.EqualTo(reference), $"seed {seed}: shared commits, storage applied early");
                }
            }

            TestContext.Out.WriteLine($"150 blocks: {shared} shared commits, {applied} slots applied or reused early");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(shared, Is.GreaterThan(500), "precondition: commits were shared");
                Assert.That(applied, Is.GreaterThan(500), "precondition: the early apply ran");
            }
        }
        finally
        {
            IdleStorageApplier.MinIdleGap = gap;
        }
    }

    private static void RandomChange(IWorldState state, Random rng)
    {
        switch (rng.Next(8))
        {
            case 0:
                // Zero credits touch: the empty account is removed (EIP-161), the missing one created empty.
                state.AddToBalanceAndCreateIfNotExists(rng.Next(2) == 0 ? Empty : Missing, (UInt256)rng.Next(0, 2), Spec);
                break;
            case 1:
                state.AddToBalance(Funded, (UInt256)rng.Next(1, 3), Spec);
                break;
            case 2:
                state.IncrementNonce(Funded, 1, out _);
                break;
            case 3:
                state.Get(new StorageCell(Contracts[rng.Next(Contracts.Length)], (UInt256)rng.Next(Slots)), out _);
                break;
            default:
                // Zero deletes a slot; the genesis values make writing a slot back to where it was common.
                state.Set(new StorageCell(Contracts[rng.Next(Contracts.Length)], (UInt256)rng.Next(Slots)), (UInt256)rng.Next(0, 4));
                break;
        }
    }

    private sealed class Context : IDisposable
    {
        private readonly IContainer _container;
        private readonly CapturingScopeProvider _scopes;
        private readonly WorldState _state;
        private readonly BlockHeader _baseBlock;

        public Context(bool applyEarly)
        {
            ConfigProvider configProvider = new();
            IFlatDbConfig flatDbConfig = configProvider.GetConfig<IFlatDbConfig>();
            flatDbConfig.Enabled = true;
            flatDbConfig.ApplyStorageWritesOnIdleThread = applyEarly;
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton<IStateHeaderProvider>(UnavailableStateHeaderProvider.Instance)
                .Build();
            // The global world state is the one main block processing uses, whose scopes apply storage writes early.
            _scopes = new CapturingScopeProvider(_container.Resolve<IWorldStateManager>().GlobalWorldState);
            _state = new WorldState(_scopes, LimboLogs.Instance);
            using (_state.BeginScope(IWorldState.PreGenesis))
            {
                _state.CreateAccount(Funded, 100);
                _state.CreateAccount(Empty, 0);
                foreach (Address contract in Contracts)
                {
                    _state.CreateAccount(contract, 0);
                    _state.InsertCode(contract, Code, Spec);
                    for (int i = 0; i < Slots - 2; i++) _state.Set(new StorageCell(contract, (UInt256)i), (UInt256)(i % 4));
                }

                // As genesis, which keeps the empty account.
                _state.Commit(Spec, NullStateTracer.Instance, isGenesis: true);
                _state.CommitTree(0);
                _baseBlock = Build.A.BlockHeader.WithStateRoot(_state.StateRoot).TestObject;
            }
        }

        /// <summary>A block of random transactions on the base state, committed after each, or only when one's changes do not commit as they read.</summary>
        public Hash256 Run(Random rng, bool share, out int earlyApplied, out int sharedCommits)
        {
            sharedCommits = 0;
            using IDisposable scope = _state.BeginScope(_baseBlock);
            FlatWorldStateScope flatScope = _scopes.Last ?? throw new InvalidOperationException("no flat scope");
            int transactions = rng.Next(1, 32);
            for (int tx = 0; tx < transactions; tx++)
            {
                Evm.State.Snapshot snapshot = _state.TakeSnapshot();
                int changes = rng.Next(1, 7);
                for (int i = 0; i < changes; i++) RandomChange(_state, rng);
                if (!share || !_state.ChangesCommitAsReadSince(in snapshot)) _state.Commit(Spec, NullStateTracer.Instance, commitRoots: false);
                else sharedCommits++;
                // Lets the idle thread apply what was committed, so the block-end batch meets early trees at every stage.
                if (rng.Next(3) == 0) Thread.Sleep(1);
            }

            _state.Commit(Spec);
            _state.RecalculateStateRoot();
            (int applied, int reused, _, _) = flatScope.EarlyApplyCounts;
            earlyApplied = applied + reused;
            return _state.StateRoot;
        }

        public void Dispose() => _container.Dispose();
    }

    /// <summary>Remembers the flat scope it last began, to read its early apply counters.</summary>
    private sealed class CapturingScopeProvider(IWorldStateScopeProvider inner) : IWorldStateScopeProvider
    {
        public FlatWorldStateScope? Last { get; private set; }

        public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(baseBlock);

        public bool HasStateForTargetBlock(BlockHeader targetBlock) => inner.HasStateForTargetBlock(targetBlock);

        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            bool begun = inner.TryBeginScopeAtTarget(targetBlock, metrics, out scope);
            Last = scope as FlatWorldStateScope;
            return begun;
        }

        public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            bool begun = inner.TryBeginScope(baseBlock, metrics, out scope);
            Last = scope as FlatWorldStateScope;
            return begun;
        }
    }
}
