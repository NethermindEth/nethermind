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
    SendPolicyJournal journal,
    ISendPolicyConfig config,
    IEthereumEcdsa ecdsa,
    ILogManager logManager) : ITxSender
{
    private static readonly AcceptTxResult Refused = new("send policy");
    private readonly ILogger _logger = logManager.GetClassLogger<SendPolicyTxSender>();

    public async ValueTask<(Hash256 Hash, AcceptTxResult? AddTxResult)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
    {
        if (tx.SenderAddress is null && ecdsa.TryRecoverAddress(tx, out Address? sender)) tx.SenderAddress = sender;
        if (tx.AuthorizationList is not null)
        {
            foreach (AuthorizationTuple tuple in tx.AuthorizationList) tuple.Authority ??= ecdsa.RecoverAddress(tuple);
        }

        SendPolicyRules rules = ruleFile.Current;
        string? refusal = SendPolicyCheck.Refusal(tx, rules);
        SendPolicyJournal.Entry? entry = null;
        if (tx.SenderAddress is not null && rules.GuardedSenders.Contains(tx.SenderAddress) && (refusal is null || config.WarnOnly))
        {
            ulong? nonce = (txHandlingOptions & TxHandlingOptions.ManagedNonce) != 0 ? null : tx.Nonce;
            string? overCap = journal.Reserve(tx.SenderAddress, nonce, SendPolicyCheck.MaxOutflow(tx), rules.Cap, !config.WarnOnly, out entry);
            refusal ??= overCap;
        }

        if (refusal is not null)
        {
            Hash256 hash = tx.Hash ?? tx.CalculateHash();
            if (_logger.IsWarn) _logger.Warn($"SendPolicy {(config.WarnOnly ? "would refuse" : "refused")} {hash} from {tx.SenderAddress}: {refusal}");
            if (!config.WarnOnly) return (hash, Refused.WithMessage(refusal));
        }

        if (entry is null) return await inner.SendTransaction(tx, txHandlingOptions);

        bool accepted = false;
        try
        {
            (Hash256 Hash, AcceptTxResult? AddTxResult) result = await inner.SendTransaction(tx, txHandlingOptions);
            accepted = result.AddTxResult?.Equals(AcceptTxResult.Accepted) == true;
            return result;
        }
        finally
        {
            if (accepted) journal.Commit(entry, tx.Nonce);
            else journal.Release(entry);
        }
    }
}
