// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    /// <summary>
    /// A witness re-execution that does not reproduce the block's state root is reported as a witness error.
    /// </summary>
    [Test]
    public async Task Witness_re_execution_mismatch_is_reported_as_a_witness_error()
    {
        PinnedRoot pinnedRoot = new();
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance, configurer: builder => builder
            // The real factory and everything it resolves, over a state whose scope at one parent keeps reporting
            // the root it was opened at.
            .AddSingleton<IWitnessGeneratingBlockProcessingEnvFactory>(ctx => new WitnessGeneratingBlockProcessingEnvFactory(
                ctx.Resolve<ILifetimeScope>(),
                new RootPinningWorldStateManager(ctx.Resolve<IWorldStateManager>(), pinnedRoot),
                ctx.Resolve<IBlockValidationModule[]>(),
                ctx.Resolve<ILogManager>())));
        BlockHeader parent = (await ProduceCanonicalPayload(chain, WitnessTransfer(chain, 0))).Block.Header;
        Block target = (await ProduceCanonicalPayload(chain, WitnessTransfer(chain, 1))).Block;
        Assert.That(target.Header.StateRoot, Is.Not.EqualTo(parent.StateRoot), "precondition: the target block changes the state");

        pinnedRoot.At = parent.StateRoot;
        IDebugRpcModule debug = chain.Container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();
        Exception? failure = null;
        try
        {
            using ResultWrapper<Witness> _ = debug.debug_executionWitness(new BlockParameter(target.Number));
        }
        catch (Exception e)
        {
            failure = e;
        }

        pinnedRoot.At = null;

        Assert.That(failure, Is.InstanceOf<InvalidBlockException>(),
            "the caller gets an error about the witness, and the block is not marked invalid");

        using ResultWrapper<Witness> recent = debug.debug_executionWitness(new BlockParameter(target.Number));
        Assert.That(recent.Result.ResultType, Is.EqualTo(ResultType.Success), "a block re-executing correctly still gets its witness");
    }

    /// <summary>
    /// Every processing environment resolves the same block validator, so a post-execution check failing in any of
    /// them leaves nothing behind; the node's own import reports its failures by itself.
    /// </summary>
    [Test]
    public async Task Failed_post_execution_check_through_the_shared_validator_has_no_side_effects()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IInvalidChainTracker tracker = chain.Container.Resolve<IInvalidChainTracker>();
        Block canonical = (await ProduceCanonicalPayload(chain, WitnessTransfer(chain, 0))).Block;

        Block processed = canonical.WithReplacedHeader(canonical.Header.Clone());
        processed.Header.StateRoot = TestItem.KeccakG;
        processed.Header.Hash = processed.Header.CalculateHash();
        bool valid = chain.Container.Resolve<IBlockValidator>()
            .ValidateProcessedBlock(processed, chain.ReceiptStorage.Get(canonical), canonical, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.False, error);
            Assert.That(tracker.IsOnKnownInvalidChain(canonical.Hash!, out _), Is.False);
        }

        Block next = (await ProduceCanonicalPayload(chain)).Block;
        Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(next.Hash));
    }

    /// <summary>Guard, passes before and after the change: a witness of a valid block is unchanged.</summary>
    [Test]
    public async Task Witness_of_an_imported_block_equals_the_witness_captured_on_import()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        chain.AddTransactions(WitnessTransfer(chain, 0));
        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain, 1);
        using ResultWrapper<NewPayloadWithWitnessV1Result> imported =
            await chain.EngineRpcModule.engine_newPayloadWithWitnessV5(payload, [], TestItem.KeccakE, requests ?? []);
        Assert.That(imported.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Witness captured = imported.Data.ExecutionWitness!;
        Hash256 blockHash = payload.BlockHash!;

        using ResultWrapper<Witness> generated = chain.Container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create()
            .debug_executionWitness(new BlockParameter(blockHash));

        Assert.That(generated.Result.ResultType, Is.EqualTo(ResultType.Success), generated.Result.Error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated.Data.State.ToArray(), Is.EqualTo(captured.State.ToArray()));
            Assert.That(generated.Data.Codes.ToArray(), Is.EqualTo(captured.Codes.ToArray()));
            Assert.That(generated.Data.Keys.ToArray(), Is.EqualTo(captured.Keys.ToArray()));
            Assert.That(generated.Data.Headers.ToArray(), Is.EqualTo(captured.Headers.ToArray()));
        }
    }

    private static Transaction WitnessTransfer(MergeTestBlockchain chain, ulong nonce) =>
        Build.A.Transaction
            .WithNonce(nonce)
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .WithMaxFeePerGas(20.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithType(TxType.EIP1559)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

    /// <summary>Builds, imports and canonicalizes one Amsterdam block, asserting both answers are VALID.</summary>
    private static async Task<(Block Block, ExecutionPayloadV4 Payload, byte[][] Requests)> ProduceCanonicalPayload(
        MergeTestBlockchain chain, params Transaction[] txs)
    {
        if (txs.Length > 0) chain.AddTransactions(txs);
        (ExecutionPayloadV4 payload, byte[][]? requests) = await BuildAmsterdamPayload(chain, txs.Length);
        Assert.That(payload.Transactions, Has.Length.EqualTo(txs.Length));

        using ResultWrapper<PayloadStatusV1> status = await chain.EngineRpcModule.engine_newPayloadV5(payload, [], TestItem.KeccakE, requests ?? []);
        Assert.That(status.Data.Status, Is.EqualTo(PayloadStatus.Valid));

        Task txPoolHeadWait = chain.WaitForTxPoolHead(payload.BlockHash!);
        using ResultWrapper<ForkchoiceUpdatedV1Result> forkchoice = await chain.EngineRpcModule.engine_forkchoiceUpdatedV4(
            new ForkchoiceStateV1(payload.BlockHash!, payload.BlockHash!, payload.BlockHash!), null);
        Assert.That(forkchoice.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        await txPoolHeadWait;

        return (chain.BlockTree.FindBlock(payload.BlockHash!)!, payload, requests ?? []);
    }

    private sealed class PinnedRoot
    {
        public Hash256? At { get; set; }
    }

    private sealed class RootPinningWorldStateManager(IWorldStateManager inner, PinnedRoot pinnedRoot) : IWorldStateManager
    {
        public IWorldStateScopeProvider GlobalWorldState => inner.GlobalWorldState;
        public IStateReader GlobalStateReader => inner.GlobalStateReader;
        public ISnapStateServer SnapStateServer => inner.SnapStateServer;
        public IReadOnlyKeyValueStore? HashServer => inner.HashServer;
        public IWorldStateScopeProvider CreateResettableWorldState() => new RootPinningScopeProvider(inner.CreateResettableWorldState(), pinnedRoot);
        public IWorldStateScopeProvider CreateReadOnlyQueryWorldState() => inner.CreateReadOnlyQueryWorldState();
        public IOverridableWorldScope CreateOverridableWorldScope() => inner.CreateOverridableWorldScope();
        public IReadOnlyTrieStore CreateReadOnlyTrieStore() => inner.CreateReadOnlyTrieStore();
        public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) => inner.VerifyTrie(stateAtBlock, cancellationToken);
        public void FlushCache(CancellationToken cancellationToken) => inner.FlushCache(cancellationToken);
        public void DropStateNotReachableFrom(BlockHeader head) => inner.DropStateNotReachableFrom(head);
    }

    private sealed class RootPinningScopeProvider(IWorldStateScopeProvider inner, PinnedRoot pinnedRoot) : IWorldStateScopeProvider, IDisposable
    {
        public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(baseBlock);

        public bool HasStateForTargetBlock(BlockHeader targetBlock) => inner.HasStateForTargetBlock(targetBlock);

        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
            inner.TryBeginScopeAtTarget(targetBlock, metrics, out scope);

        public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            if (!inner.TryBeginScope(baseBlock, metrics, out scope)) return false;
            if (baseBlock?.StateRoot is { } root && root == pinnedRoot.At) scope = new PinnedRootScope(scope, root);
            return true;
        }

        public void Dispose() => (inner as IDisposable)?.Dispose();
    }

    /// <summary>Reports the root it was opened at, whatever is written.</summary>
    private sealed class PinnedRootScope(IWorldStateScopeProvider.IScope inner, Hash256 baseRoot) : IWorldStateScopeProvider.IScope
    {
        public Hash256 RootHash => baseRoot;
        public bool StorageRootsAreAuthoritative => inner.StorageRootsAreAuthoritative;
        public void UpdateRootHash() { }
        public void HintWarmAccount(Address address) => inner.HintWarmAccount(address);
        public void HintWarmSlot(Address address, in UInt256 index) => inner.HintWarmSlot(address, in index);
        public void Prefetch(Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken) => inner.Prefetch(accounts, slots, cancellationToken);
        public bool SupportsPrefetch => inner.SupportsPrefetch;
        public Account? Get(Address address) => inner.Get(address);
        public void HintGet(Address address, Account? account) => inner.HintGet(address, account);
        public IWorldStateScopeProvider.ICodeDb CodeDb => inner.CodeDb;
        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => inner.CreateStorageTree(address);
        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => inner.StartWriteBatch(estimatedAccountNum);
        public void Commit(ulong blockNumber) => inner.Commit(blockNumber);
        public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot) => inner.WriteBackCommittedState(takeSnapshot);
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => inner.HintBal(bal, sink);
        public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken = default) => inner.ApplyBal(bal, cancellationToken);
        public void Dispose() => inner.Dispose();
    }
}
