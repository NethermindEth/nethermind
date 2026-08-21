// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Validators;

/// <summary>
/// Validates transaction properties whose validity can change when the release specification changes.
/// </summary>
public sealed class SpecChangeTxValidator() :
    CompositeTxValidator(
        MaxBlobCountBlobTxValidator.Instance,
        GasLimitCapTxValidator.Instance,
        MempoolBlobTxProofVersionValidator.Instance,
        IntrinsicGasTxValidator.Instance)
{
    public static readonly SpecChangeTxValidator Instance = new();
}
