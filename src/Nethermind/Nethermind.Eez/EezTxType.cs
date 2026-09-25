// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.TxPool;

namespace Nethermind.Eez;

public static class EezTxType
{
    public static ITxDecoder CreateDecoder() => new EezSystemTxDecoder<Transaction>();

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
}
