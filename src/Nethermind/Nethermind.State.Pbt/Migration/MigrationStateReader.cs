// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Reads the backend that commits to the requested header's state root.</summary>
/// <remarks>
/// <see cref="HasStateForBlock"/> answers exactly what <see cref="MigrationScopeProvider"/> can open for a null
/// target, so the processing pre-checks that go through the state reader agree with main processing.
/// </remarks>
internal sealed class MigrationStateReader(FlatStateReader flat, PbtStateReader pbt, MigrationBackendSelector selector) : IStateReader
{
    // Flat never holds a post-activation state, so availability picks the backend; flat first, as the authoritative tree before activation.
    private IStateReader Select(BlockHeader? baseBlock) => flat.HasStateForBlock(baseBlock) ? flat : pbt;

    public bool HasStateForBlock(BlockHeader? baseBlock) => selector.IsBinary(baseBlock, null)
        ? pbt.HasStateForBlock(baseBlock)
        : flat.HasStateForBlock(baseBlock) && (selector.PbtHas(baseBlock) || selector.PbtAhead(baseBlock));

    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account) =>
        Select(baseBlock).TryGetAccount(baseBlock, address, out account);

    public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value) =>
        Select(baseBlock).GetStorage(baseBlock, address, index, out value);

    public byte[]? GetCode(Hash256 codeHash) => flat.GetCode(codeHash);

    public byte[]? GetCode(in ValueHash256 codeHash) => flat.GetCode(codeHash);

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock,
        VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx>
    {
        if (!flat.HasStateForBlock(baseBlock)) throw new NotSupportedException("MPT proofs are not available for the PBT state backend");
        flat.RunTreeVisitor(treeVisitor, baseBlock, visitingOptions, diagnostics);
    }
}
