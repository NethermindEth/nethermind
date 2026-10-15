// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>The checks a request's fee fields pass before its missing fees are filled as for a transaction to send.</summary>
/// <remarks>
/// These rules belong to filling fees, not to executing them, so a malformed fee pair is reported with its values in
/// hexadecimal, as the request carried them. A request whose missing fee the fee oracle would supply passes, and
/// the caller handles it as before.
/// </remarks>
internal static class FeeDefaultRules
{
    private const string ZeroMaxFeePerGas = "maxFeePerGas must be non-zero";
    private const string ZeroGasPriceAfterLondon = "gasPrice must be non-zero after london fork";
    private const string FeeFieldsBeforeLondon = "maxFeePerGas and maxPriorityFeePerGas are not valid before London is active";
    private const string GasPriceWithAuthorizationList = "both gasPrice and authorizationList specified";

    /// <summary>The first rule <paramref name="call"/> breaks, or null when its fee fields pass or are checked elsewhere.</summary>
    /// <param name="isLondon">Whether the block the fees are filled against has a base fee.</param>
    public static string? Error(TransactionForRpc call, bool isLondon)
    {
        // A zero blob fee cap and a gas price next to fee fields fail earlier, with the same text, in input validation.
        if (call is BlobTransactionForRpc { MaxFeePerBlobGas: { IsZero: true } } || call is not LegacyTransactionForRpc legacy)
            return null;

        UInt256? gasPrice = legacy.GasPrice;
        UInt256? maxFeePerGas = (call as EIP1559TransactionForRpc)?.MaxFeePerGas;
        UInt256? maxPriorityFeePerGas = (call as EIP1559TransactionForRpc)?.MaxPriorityFeePerGas;
        if (gasPrice is not null && (maxFeePerGas is not null || maxPriorityFeePerGas is not null))
            return null;

        if (gasPrice is not null && call is SetCodeTransactionForRpc { AuthorizationList: not null })
            return GasPriceWithAuthorizationList;

        if (gasPrice is null && maxFeePerGas is { } feeCap && maxPriorityFeePerGas is { } priorityFee)
            return feeCap.IsZero ? ZeroMaxFeePerGas : OrderError(feeCap, priorityFee);

        if (gasPrice is not null)
            return gasPrice.Value.IsZero && isLondon ? ZeroGasPriceAfterLondon : null;

        return !isLondon && (maxFeePerGas is not null || maxPriorityFeePerGas is not null) ? FeeFieldsBeforeLondon : null;
    }

    /// <summary>The error for a fee cap below the priority fee, or null when they are in order.</summary>
    public static string? OrderError(in UInt256 maxFeePerGas, in UInt256 maxPriorityFeePerGas) =>
        maxFeePerGas < maxPriorityFeePerGas
            ? $"maxFeePerGas ({maxFeePerGas.ToHexString(skipLeadingZeros: true)}) < maxPriorityFeePerGas ({maxPriorityFeePerGas.ToHexString(skipLeadingZeros: true)})"
            : null;
}
