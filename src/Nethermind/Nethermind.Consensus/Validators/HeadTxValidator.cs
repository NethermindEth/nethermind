// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public sealed class HeadTxValidator() :
    CompositeTxValidator(Validators)
{
    internal static readonly ITxValidator[] Validators = [
        ReleaseSpecTxValidator.Instance,
        MaxBlobCountBlobTxValidator.Instance,
        new ExceptFrameTxValidator(GasLimitCapTxValidator.Instance),
        MempoolBlobTxProofVersionValidator.Instance,
        FrameTxNonceKeysTxValidator.Instance
    ];
}
