// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.TxPool;

/// <summary>
/// Rejects transactions whose sender or recipient is blacklisted, keeping them out of the pool and out of gossip.
/// </summary>
/// <remarks>
/// The blacklist is a consensus rule enforced during execution by <see cref="XdcTransactionProcessor.ValidateSender"/>;
/// this filter is the mempool-admission counterpart, so that such transactions are dropped on submission rather than
/// when a block containing them is processed. Activation is read from the spec at the current head.
/// </remarks>
internal sealed class BlackListedAddressFilter(IBlockTree blockTree, ISpecProvider specProvider) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (blockTree.Head is null)
            return AcceptTxResult.Syncing;

        IXdcReleaseSpec spec = specProvider.GetXdcSpec(blockTree.Head.Number);

        if (!spec.IsBlackListingEnabled)
            return AcceptTxResult.Accepted;

        if (IsBlackListed(spec, tx.SenderAddress))
            return AcceptTxResult.Invalid.WithMessage("Transaction sender is blacklisted");

        if (IsBlackListed(spec, tx.To))
            return AcceptTxResult.Invalid.WithMessage("Transaction recipient is blacklisted");

        return AcceptTxResult.Accepted;
    }

    private static bool IsBlackListed(IXdcReleaseSpec spec, Address? address) =>
        address is not null && spec.BlackListedAddresses.Contains(address);
}
