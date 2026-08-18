// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
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
/// when a block containing them is processed. Activation is read from the spec of the block the transaction would land
/// in, one past the current head, matching <see cref="SignTransactionFilter"/>.
/// </remarks>
internal sealed class BlackListedAddressFilter(
    IChainHeadInfoProvider chainHeadInfoProvider,
    ISpecProvider specProvider,
    ILogManager logManager) : IIncomingTxFilter
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlackListedAddressFilter>();

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(chainHeadInfoProvider.HeadNumber + 1);

        if (!spec.IsBlackListingEnabled)
            return AcceptTxResult.Accepted;

        if (IsBlackListed(spec, tx.SenderAddress))
            return Reject(tx, "sender");

        if (IsBlackListed(spec, tx.To))
            return Reject(tx, "recipient");

        return AcceptTxResult.Accepted;
    }

    private AcceptTxResult Reject(Transaction tx, string role)
    {
        if (_logger.IsDebug) _logger.Debug($"Skipped adding transaction {tx.ToString("  ")}, blacklisted {role}.");
        return XdcAcceptTxResult.BlackListedAddress.WithMessage($"Transaction {role} is blacklisted");
    }

    private static bool IsBlackListed(IXdcReleaseSpec spec, Address? address) =>
        address is not null && spec.BlackListedAddresses.Contains(address);
}
