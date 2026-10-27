// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement.RollupX;

namespace Nethermind.Eez.Execution;

public static class EezTransactionExtensions
{
    public static bool IsEezSystemTransaction(this Transaction tx) =>
        tx.Type is EezConstants.SystemTxType or EezConstants.NativeSystemTxType || (byte)tx.Type == NativeHistoryEnvelope.Type;

    /// <summary>
    /// Whether the envelope and its implicit fields match the chain's system transaction profile.
    /// </summary>
    /// <remarks>
    /// The decoder always produces them; a transaction built any other way (RPC, block production, tests)
    /// must not reach execution with a different sender, target, budget or price.
    /// </remarks>
    public static bool HasSystemTransactionFields(this Transaction tx, EezChainRules rules) =>
        (tx.Type == rules.SystemTransactionType || rules.UsesNativeContractV3 && tx is NativeHistoryTransaction && (byte)tx.Type == NativeHistoryEnvelope.Type)
        && tx.SenderAddress == rules.SystemAddress
        && (rules.UsesNativeSystemTransactions ? HasNativeEnvelopeFields(tx, rules) : tx.To == rules.Eezl2Address)
        && rules.IsSystemTransactionGas(tx.GasLimit)
        && tx.GasPrice.IsZero
        && tx.DecodedMaxFeePerGas.IsZero
        && tx.AccessList is null
        && tx.AuthorizationList is null
        && tx.BlobVersionedHashes is null;

    private static bool HasNativeEnvelopeFields(Transaction tx, EezChainRules rules) =>
        (tx is NativeHistoryTransaction history
            ? rules.UsesNativeContractV3 && tx.SourceHash is null && tx.ChainId == history.Envelope.ChainId
                && tx.To == EezConstants.L1HistoryAddress && tx.ValueRef.IsZero && tx.GasLimit == EezConstants.L1HistoryTxGasCap
                && (tx.Data.IsEmpty || history.IsExecutionView)
            : tx.SourceHash is not null && (tx.To == rules.Eezl2Address || !rules.UsesNativeContractV3 && tx.To == EezConstants.L1HistoryAddress))
        && tx.Nonce == 0 && tx.Signature is null && tx.MaxFeePerBlobGas is null
        && (tx.To == rules.Eezl2Address || tx.To == EezConstants.L1HistoryAddress && tx.ValueRef.IsZero
            && tx.GasLimit <= EezConstants.L1HistoryTxGasCap);
}
