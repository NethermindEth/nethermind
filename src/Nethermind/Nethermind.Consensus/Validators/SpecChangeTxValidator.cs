// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

/// <summary>
/// Validates transaction properties whose validity can change when the release specification changes.
/// </summary>
public sealed class SpecChangeTxValidator() :
    CompositeTxValidator([.. HeadTxValidator.Validators, IntrinsicGasTxValidator.Instance]), ILightTxValidator
{
    private static readonly HeadTxValidator LightTxValidator = new();

    public static readonly SpecChangeTxValidator Instance = new();

    public ValidationResult IsWellFormedLight(LightTransaction transaction, IReleaseSpec releaseSpec) =>
        LightTxValidator.IsWellFormed(transaction, releaseSpec);
}
