// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa-flavoured RPC block model: emits the <c>step</c> + <c>signature</c> + <c>author</c> seal
/// in place of the Ethash <c>mixHash</c> + <c>nonce</c> the base model writes.
/// </summary>
public sealed class AuRaBlockForRpc : BlockForRpc
{
    public AuRaBlockForRpc(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false)
        : base(block, includeFullTransactionData, specProvider, skipTxs)
    {
        MixHash = null;
        Nonce = null;
        Author = block.Author;
        AuRaBlockHeader aura = (AuRaBlockHeader)block.Header;
        Step = aura.AuRaStep;
        Signature = aura.AuRaSignature;
    }
}

/// <summary><inheritdoc cref="AuRaBlockForRpc"/></summary>
public sealed class AuRaBlockHeaderForRpc : BlockHeaderForRpc
{
    public AuRaBlockHeaderForRpc(AuRaBlockHeader header, ISpecProvider? specProvider = null)
        : base(header, specProvider)
    {
        MixHash = null;
        Nonce = null;
        Author = header.Author;
        Step = header.AuRaStep;
        Signature = header.AuRaSignature;
    }
}

/// <summary>
/// Produces <see cref="AuRaBlockForRpc"/> / <see cref="AuRaBlockHeaderForRpc"/> for AuRa-sealed headers,
/// falling back to the base (Ethash/PoS) models for the post-merge headers of merged AuRa chains.
/// </summary>
public sealed class AuRaBlockForRpcFactory : BlockForRpcFactory
{
    public override BlockForRpc Create(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false) =>
        block.Header is AuRaBlockHeader
            ? new AuRaBlockForRpc(block, includeFullTransactionData, specProvider, skipTxs)
            : base.Create(block, includeFullTransactionData, specProvider, skipTxs);

    public override BlockHeaderForRpc CreateHeader(BlockHeader header, ISpecProvider? specProvider = null) =>
        header is AuRaBlockHeader aura
            ? new AuRaBlockHeaderForRpc(aura, specProvider)
            : base.CreateHeader(header, specProvider);
}
