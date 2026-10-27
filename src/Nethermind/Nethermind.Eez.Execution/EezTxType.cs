// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.TxPool;

namespace Nethermind.Eez.Execution;

/// <summary>Constructs the decoder and validators for unsigned EEZ system transactions.</summary>
public static class EezTxType
{
    /// <summary>Decodes the EEZ typed envelope and fixes its unsigned system fields.</summary>
    public static ITxDecoder CreateDecoder() => new EezSystemTxDecoder<Transaction>();

    internal static ITxDecoder CreateDecoder(EezChainRules rules) =>
        rules.UsesNativeSystemTransactions ? new EezNativeSystemTxDecoder() : CreateDecoder();

    /// <summary>Validates chain ID, nonce, gas limit and intrinsic gas for an EEZ system transaction.</summary>
    /// <remarks>
    /// There is no signature to check: the decoder fixes the sender, target, budget and price, and
    /// <see cref="EezTransactionProcessor"/> re-checks them.
    /// </remarks>
    public static ITxValidator CreateValidator(ulong chainId) => new CompositeTxValidator([
        NonceCapTxValidator.Instance,
        new ExpectedChainIdTxValidator(chainId),
        GasLimitCapTxValidator.Instance,
        IntrinsicGasTxValidator.Instance
    ]);

    internal static ITxValidator CreateValidator(ulong chainId, EezChainRules rules) =>
        rules.UsesNativeSystemTransactions
            ? new CompositeTxValidator([
                new NativeFieldsValidator(rules),
                new ExpectedChainIdTxValidator(chainId),
                GasLimitCapTxValidator.Instance,
                IntrinsicGasTxValidator.Instance
            ])
            : CreateValidator(chainId);

    private sealed class NativeFieldsValidator(EezChainRules rules) : ITxValidator
    {
        public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec) =>
            transaction.HasSystemTransactionFields(rules)
                ? ValidationResult.Success
                : "Invalid EEZ native system transaction fields.";
    }
}
