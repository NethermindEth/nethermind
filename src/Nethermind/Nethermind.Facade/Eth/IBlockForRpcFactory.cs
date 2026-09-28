// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Facade.Eth;

/// <summary>
/// Builds the RPC block/header models. The default produces the Ethash/PoS shape; consensus plugins
/// override to emit engine-specific seal fields (e.g. AuRa <c>step</c> + <c>signature</c>) without
/// the shared models needing to know the engine-specific header type.
/// </summary>
public interface IBlockForRpcFactory
{
    BlockForRpc Create(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false);
    BlockHeaderForRpc CreateHeader(BlockHeader header, ISpecProvider? specProvider = null);
}

/// <summary>Default factory producing the seal-agnostic <see cref="BlockForRpc"/> / <see cref="BlockHeaderForRpc"/>.</summary>
public class BlockForRpcFactory : IBlockForRpcFactory
{
    public virtual BlockForRpc Create(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false) =>
        new(block, includeFullTransactionData, specProvider, skipTxs);

    public virtual BlockHeaderForRpc CreateHeader(BlockHeader header, ISpecProvider? specProvider = null) =>
        new(header, specProvider);
}
