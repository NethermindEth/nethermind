// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.SendPolicy.Plugin;

/// <summary>
/// Wraps the node's <see cref="ITxSender"/> and refuses what <see cref="SendPolicyCheck"/> refuses.
/// </summary>
/// <remarks>
/// Every JSON-RPC send path goes through <see cref="ITxSender"/>. Transactions from peers and re-added after a
/// reorg enter the pool directly and are not checked.
/// </remarks>
public sealed class SendPolicyTxSender(
    ITxSender inner,
    SendPolicyRuleFile ruleFile,
    ISendPolicyConfig config,
    IEthereumEcdsa ecdsa,
    ILogManager logManager) : ITxSender
{
    private static readonly AcceptTxResult Refused = new("send policy");
    private readonly ILogger _logger = logManager.GetClassLogger<SendPolicyTxSender>();

    public ValueTask<(Hash256 Hash, AcceptTxResult? AddTxResult)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
    {
        if (tx.SenderAddress is null && ecdsa.TryRecoverAddress(tx, out Address? sender)) tx.SenderAddress = sender;
        if (tx.AuthorizationList is not null)
        {
            foreach (AuthorizationTuple tuple in tx.AuthorizationList) tuple.Authority ??= ecdsa.RecoverAddress(tuple);
        }

        string? refusal = SendPolicyCheck.Refusal(tx, ruleFile.Current);
        if (refusal is null) return inner.SendTransaction(tx, txHandlingOptions);

        Hash256 hash = tx.Hash ?? tx.CalculateHash();
        if (_logger.IsWarn) _logger.Warn($"SendPolicy {(config.WarnOnly ? "would refuse" : "refused")} {hash} from {tx.SenderAddress}: {refusal}");
        return config.WarnOnly
            ? inner.SendTransaction(tx, txHandlingOptions)
            : new((hash, Refused.WithMessage(refusal)));
    }
}
