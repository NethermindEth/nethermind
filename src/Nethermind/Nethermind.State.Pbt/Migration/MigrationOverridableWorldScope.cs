// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Override environment that keeps a flat and a PBT overlay and selects one by the requested block's spec.</summary>
/// <remarks>
/// The overridable env opens its scope with a null target, so the backend is the one holding the base state. A
/// request that crosses activation from there (an <c>eth_simulateV1</c> block list or a block override) stays on
/// that backend; its writes are identical, only the reported state root is the other tree's.
/// </remarks>
internal sealed class MigrationOverridableWorldScope(FlatOverridableWorldScope flat, PbtOverridableWorldScope pbt, ISpecProvider specProvider)
    : IOverridableWorldScope
{
    public IWorldStateScopeProvider WorldState { get; } = new MigrationReadOnlyScopeProvider(flat.WorldState, pbt.WorldState, specProvider);
    public IStateReader GlobalStateReader { get; } = new Reader(flat.GlobalStateReader, pbt.GlobalStateReader);

    public void ResetOverrides()
    {
        try { flat.ResetOverrides(); }
        finally { pbt.ResetOverrides(); }
    }

    public void Dispose()
    {
        try { flat.Dispose(); }
        finally { pbt.Dispose(); }
    }

    private sealed class Reader(IStateReader flat, IStateReader pbt) : IStateReader
    {
        private IStateReader Select(BlockHeader? baseBlock) => flat.HasStateForBlock(baseBlock) ? flat : pbt;

        public bool HasStateForBlock(BlockHeader? baseBlock) => Select(baseBlock).HasStateForBlock(baseBlock);

        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account) =>
            Select(baseBlock).TryGetAccount(baseBlock, address, out account);

        public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value) =>
            Select(baseBlock).GetStorage(baseBlock, address, index, out value);

        public byte[]? GetCode(Hash256 codeHash) => flat.GetCode(codeHash);

        public byte[]? GetCode(in ValueHash256 codeHash) => flat.GetCode(codeHash);

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock,
            VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx> =>
            Select(baseBlock).RunTreeVisitor(treeVisitor, baseBlock, visitingOptions, diagnostics);
    }
}
