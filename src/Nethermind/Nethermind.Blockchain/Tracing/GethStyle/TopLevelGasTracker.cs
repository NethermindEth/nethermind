// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Tracks the execution-gas delta of the top-level EVM action.
/// </summary>
/// <remarks>
/// Store this mutable value type in a writable field and mutate it in place; copying it also copies its action-depth state.
/// </remarks>
/// <param name="standardIntrinsicGas">
/// Standard transaction intrinsic gas used when execution ends before a top-level action is traced.
/// </param>
public struct TopLevelGasTracker(ulong? standardIntrinsicGas)
{
    private int _actionDepth;
    private ulong _topLevelActionGas;
    private bool _hasTopLevelActionResult;

    /// <summary>Calculates standard intrinsic gas when available for the active specification.</summary>
    /// <param name="transaction">Transaction being traced.</param>
    /// <param name="spec">Active specification.</param>
    /// <param name="blockGasLimit">Gas limit of the containing block.</param>
    /// <returns>
    /// Standard intrinsic gas, or <see langword="null"/> when the transaction or specification is absent or intrinsic
    /// gas cannot be calculated for the active specification.
    /// </returns>
    public static ulong? GetStandardIntrinsicGas(
        Transaction? transaction,
        IReleaseSpec? spec,
        ulong blockGasLimit = 0)
    {
        if (transaction is null || spec is null)
            return null;

        try
        {
            return IntrinsicGasCalculator.Calculate(transaction, spec, blockGasLimit).Standard;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Records entry into an EVM action.</summary>
    /// <param name="gas">Gas available to the action.</param>
    public void StartAction(ulong gas)
    {
        if (_actionDepth++ == 0)
            _topLevelActionGas = gas;
    }

    /// <summary>Records action completion and returns the top-level execution-gas delta when available.</summary>
    /// <param name="gas">Gas remaining after the action.</param>
    /// <returns>The top-level execution gas used, or <see langword="null"/> for a nested or unmatched completion.</returns>
    public ulong? EndAction(ulong gas)
    {
        if (_actionDepth <= 0 || --_actionDepth != 0)
            return null;

        _hasTopLevelActionResult = true;
        return _topLevelActionGas.SaturatingSub(gas);
    }

    /// <summary>Gets receipt-derived execution gas when no top-level action result was observed.</summary>
    /// <param name="gasSpent">Settled transaction gas.</param>
    /// <returns>The receipt-derived execution gas, or <see langword="null"/> when no fallback is applicable.</returns>
    public readonly ulong? GetReceiptFallback(in GasConsumed gasSpent) =>
        !_hasTopLevelActionResult && standardIntrinsicGas.HasValue
            ? gasSpent.SpentGas.SaturatingSub(standardIntrinsicGas.Value)
            : null;
}
