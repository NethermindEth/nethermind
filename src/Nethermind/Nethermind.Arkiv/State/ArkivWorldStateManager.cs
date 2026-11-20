// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;

namespace Nethermind.Arkiv.State;

/// <summary>
/// Where Nethermind opens state by a header, it opens the account trie by the header's <c>stateRoot</c>. On Arkiv that
/// field is GolemDB's commitment, so every such lookup goes through <see cref="Translate"/>, which swaps it for the root
/// of the local account trie recorded when the block was processed. Headers this node has not processed pass unchanged.
/// </summary>
public sealed class ArkivRoots(ArkivChain chain)
{
    [return: NotNullIfNotNull(nameof(header))]
    public BlockHeader? Translate(BlockHeader? header)
    {
        if (header?.StateRoot is not { } root) return header;
        Hash256? trieRoot = chain.TrieRootOf(root);
        if (trieRoot is null || trieRoot == root) return header;
        BlockHeader translated = header.Clone();
        translated.StateRoot = trieRoot;
        return translated;
    }
}

/// <summary><see cref="IStateHeaderProvider"/> whose headers carry local account trie roots (see <see cref="ArkivRoots"/>).</summary>
public sealed class ArkivStateHeaderProvider(IStateHeaderProvider inner, ArkivRoots roots) : IStateHeaderProvider
{
    public BlockHeader? FindParentHeader(BlockHeader target) => roots.Translate(inner.FindParentHeader(target));
    public ulong FinalizedBlockNumber => inner.FinalizedBlockNumber;
    public BlockHeader? GetFinalizedHeader(ulong blockNumber) => roots.Translate(inner.GetFinalizedHeader(blockNumber));
}

/// <summary><see cref="IWorldStateManager"/> whose scope providers and state readers open headers by local trie roots.</summary>
public sealed class ArkivWorldStateManager(IWorldStateManager inner, ArkivRoots roots) : IWorldStateManager
{
    private readonly IWorldStateScopeProvider _global = new ArkivScopeProvider(inner.GlobalWorldState, roots);
    private readonly IStateReader _reader = new ArkivStateReader(inner.GlobalStateReader, roots);

    public IWorldStateScopeProvider GlobalWorldState => _global;
    public IStateReader GlobalStateReader => _reader;
    public ISnapStateServer SnapStateServer => inner.SnapStateServer;
    public IReadOnlyKeyValueStore? HashServer => inner.HashServer;
    public IWorldStateScopeProvider CreateResettableWorldState() => new ArkivScopeProvider(inner.CreateResettableWorldState(), roots);
    public IWorldStateScopeProvider CreateReadOnlyQueryWorldState() => new ArkivScopeProvider(inner.CreateReadOnlyQueryWorldState(), roots);
    public IOverridableWorldScope CreateOverridableWorldScope() => new ArkivOverridableWorldScope(inner.CreateOverridableWorldScope(), roots);
    public IReadOnlyTrieStore CreateReadOnlyTrieStore() => inner.CreateReadOnlyTrieStore();
    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) => inner.VerifyTrie(roots.Translate(stateAtBlock), cancellationToken);
    public void FlushCache(CancellationToken cancellationToken) => inner.FlushCache(cancellationToken);
    public void DropStateNotReachableFrom(BlockHeader head) => inner.DropStateNotReachableFrom(roots.Translate(head));
}

internal sealed class ArkivOverridableWorldScope(IOverridableWorldScope inner, ArkivRoots roots) : IOverridableWorldScope
{
    public IWorldStateScopeProvider WorldState { get; } = new ArkivScopeProvider(inner.WorldState, roots);
    public IStateReader GlobalStateReader { get; } = new ArkivStateReader(inner.GlobalStateReader, roots);
    public void ResetOverrides() => inner.ResetOverrides();
    public void Dispose() => inner.Dispose();
}

internal sealed class ArkivScopeProvider(IWorldStateScopeProvider inner, ArkivRoots roots) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(roots.Translate(baseBlock));

    // The target's own root is not read; its parent comes through the decorated IStateHeaderProvider.
    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        inner.TryBeginScopeAtTarget(targetBlock, metrics, out scope);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => inner.HasStateForTargetBlock(targetBlock);

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        inner.TryBeginScope(roots.Translate(baseBlock), metrics, out scope);
}

internal sealed class ArkivStateReader(IStateReader inner, ArkivRoots roots) : IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account) => inner.TryGetAccount(roots.Translate(baseBlock), address, out account);
    public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value) => inner.GetStorage(roots.Translate(baseBlock), address, in index, out value);
    public void GetStorage(BlockHeader? baseBlock, Address address, ReadOnlySpan<UInt256> indexes, Span<UInt256> values) => inner.GetStorage(roots.Translate(baseBlock), address, indexes, values);
    public byte[]? GetCode(Hash256 codeHash) => inner.GetCode(codeHash);
    public byte[]? GetCode(in ValueHash256 codeHash) => inner.GetCode(in codeHash);

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null)
        where TCtx : struct, INodeContext<TCtx> => inner.RunTreeVisitor(treeVisitor, roots.Translate(baseBlock), visitingOptions, diagnostics);

    public void RunTreeVisitor<TCtx>(INodeRecordVisitor<TCtx> visitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions, VisitingStats? diagnostics)
        where TCtx : struct, INodeContext<TCtx> => inner.RunTreeVisitor(visitor, roots.Translate(baseBlock), visitingOptions, diagnostics);

    public bool HasStateForBlock(BlockHeader? baseBlock) => inner.HasStateForBlock(roots.Translate(baseBlock));
}
