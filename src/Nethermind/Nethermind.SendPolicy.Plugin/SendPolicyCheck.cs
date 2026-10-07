// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.SendPolicy.Plugin;

/// <summary>
/// Decides from the signed bytes alone whether a transaction may leave through this node.
/// </summary>
/// <remarks>
/// The check never simulates: a contract can branch on state that differs between submission and inclusion.
/// Sender and authorization authorities must already be recovered.
/// </remarks>
public static class SendPolicyCheck
{
    private const uint Approve = 0x095ea7b3;
    private const uint IncreaseAllowance = 0x39509351;
    private const uint SetApprovalForAll = 0xa22cb465;
    private const uint Transfer = 0xa9059cbb;
    private const uint TransferFrom = 0x23b872dd;
    private const uint SafeTransferFrom = 0x42842e0e;
    private const uint SafeTransferFromWithData = 0xb88d4fde;
    private const uint SafeTransferFrom1155 = 0xf242432a;
    private const uint SafeBatchTransferFrom1155 = 0x2eb2c2d6;

    /// <returns>The reason to refuse, or <c>null</c> to let the transaction through.</returns>
    public static string? Refusal(Transaction tx, SendPolicyRules rules)
    {
        bool senderGuarded = tx.SenderAddress is not null && rules.GuardedSenders.Contains(tx.SenderAddress);
        AuthorizationTuple? guardedDelegation = GuardedDelegation(tx, rules);
        if (!senderGuarded && guardedDelegation is null) return null;
        if (rules.Error is not null) return rules.Error;
        if (guardedDelegation is not null && !rules.Grants.Contains(guardedDelegation.CodeAddress))
            return $"delegation of {guardedDelegation.Authority} to {guardedDelegation.CodeAddress} needs 'grant {guardedDelegation.CodeAddress}'";
        return senderGuarded ? SenderRefusal(tx, rules) : null;
    }

    private static AuthorizationTuple? GuardedDelegation(Transaction tx, SendPolicyRules rules)
    {
        if (tx.AuthorizationList is null) return null;
        AuthorizationTuple? firstGuarded = null;
        foreach (AuthorizationTuple tuple in tx.AuthorizationList)
        {
            if (tuple.Authority is null || !rules.GuardedSenders.Contains(tuple.Authority)) continue;
            if (!rules.Grants.Contains(tuple.CodeAddress)) return tuple;
            firstGuarded ??= tuple;
        }

        return firstGuarded;
    }

    private static string? SenderRefusal(Transaction tx, SendPolicyRules rules)
    {
        if (tx.To is null) return "contract creation is not allowed";
        if (tx.SupportsBlobs) return "blob transaction is not allowed";
        UInt256 feeCeiling = FeeCeiling(tx);
        if (rules.MaxFee is { } maxFee && feeCeiling > maxFee) return $"fee ceiling {feeCeiling} wei is over 'fee {maxFee}'";
        if (IsCancellation(tx)) return null;
        if (!rules.Destinations.TryGetValue(tx.To, out (UInt256 MaxValue, int Line, bool TokenOnly) destination))
            return $"unlisted destination {tx.To}, add 'to {tx.To}'";
        if (tx.ValueRef > destination.MaxValue)
            return $"value {tx.ValueRef} wei is over the limit {destination.MaxValue} of rule line {destination.Line}";
        return BeneficiaryRefusal(tx.Data.Span, rules, destination.TokenOnly ? tx.To : null);
    }

    /// <returns>The most wei the transaction can take from its sender, saturated at the maximum.</returns>
    public static UInt256 MaxOutflow(Transaction tx) =>
        UInt256.AddOverflow(tx.ValueRef, FeeCeiling(tx), out UInt256 outflow) ? UInt256.MaxValue : outflow;

    private static UInt256 FeeCeiling(Transaction tx) =>
        UInt256.MultiplyOverflow(tx.MaxFeePerGas, (UInt256)tx.GasLimit, out UInt256 feeCeiling) ? UInt256.MaxValue : feeCeiling;

    private static bool IsCancellation(Transaction tx) =>
        tx.To == tx.SenderAddress && tx.ValueRef.IsZero && tx.Data.Length == 0 && tx.AuthorizationList is null;

    /// <remarks>
    /// Covers the ERC-20, ERC-721 and ERC-1155 calls that hand tokens or an allowance to an address named in the
    /// calldata. A zero <c>approve</c> or a <c>false</c> <c>setApprovalForAll</c> only revokes and always passes.
    /// At a <paramref name="tokenOnly"/> destination every other call is refused.
    /// </remarks>
    private static string? BeneficiaryRefusal(ReadOnlySpan<byte> data, SendPolicyRules rules, Address? tokenOnly)
    {
        if (data.Length < 4) return tokenOnly is null ? null : $"only token calls are allowed at 'token {tokenOnly}'";
        uint selector = BinaryPrimitives.ReadUInt32BigEndian(data);
        int beneficiaryArgument = selector switch
        {
            Approve or IncreaseAllowance or SetApprovalForAll or Transfer => 0,
            TransferFrom or SafeTransferFrom or SafeTransferFromWithData or SafeTransferFrom1155 or SafeBatchTransferFrom1155 => 1,
            _ => -1
        };
        if (beneficiaryArgument < 0) return tokenOnly is null ? null : $"call 0x{selector:x8} is not allowed at 'token {tokenOnly}'";
        if (data.Length < 4 + 32 * (beneficiaryArgument + 2)) return "short calldata for a token call";
        Address beneficiary = new(data.Slice(4 + 32 * beneficiaryArgument + 12, Address.Size));
        bool revokes = selector is Approve or SetApprovalForAll && data.Slice(4 + 32, 32).IndexOfAnyExcept((byte)0) < 0;
        return revokes || rules.Grants.Contains(beneficiary) ? null : $"{beneficiary} may not receive tokens or an allowance, add 'grant {beneficiary}'";
    }
}
