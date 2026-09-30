// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>An account as its EIP-8297 account-stem leaves: the basic-data leaf and either the code-hash or the delegation leaf.</summary>
/// <remarks>
/// Encoded as the basic-data leaf followed by the code leaf, told apart by length: 32 bytes for a codeless account, whose
/// code-hash leaf is the empty-code hash, 64 for a code-hash leaf and 55 for the 23 meaningful bytes of a delegation leaf.
/// No storage root is kept: PBT never treats one as authoritative.
/// </remarks>
public readonly record struct PbtAccount(ValueHash256 BasicData, ValueHash256 CodeLeaf, bool IsDelegation)
{
    public const int MaxEncodedLength = 2 * LeafLength;
    private const int LeafLength = 32;
    private const int DelegationLength = 23;

    public uint CodeSize => PbtKeyDerivation.ReadBasicDataCodeSize(BasicData.Bytes);

    public bool HasCode => IsDelegation || CodeLeaf != Keccak.OfAnEmptyString.ValueHash256;

    public ValueHash256 CodeHash => IsDelegation ? ValueKeccak.Compute(CodeLeaf.Bytes[..DelegationLength]) : CodeLeaf;

    public int EncodedLength => IsDelegation ? LeafLength + DelegationLength : HasCode ? 2 * LeafLength : LeafLength;

    /// <summary>The account as the world state sees it, with the empty tree as its storage root.</summary>
    public Account ToAccount()
    {
        PbtKeyDerivation.UnpackBasicData(BasicData.Bytes, out ulong nonce, out UInt256 balance);
        return new Account(nonce, balance, Keccak.EmptyTreeHash, HasCode ? new Hash256(CodeHash) : Keccak.OfAnEmptyString);
    }

    /// <param name="code">The account's code; required when the account has code.</param>
    public static PbtAccount From(Account account, CodeInfo? code)
    {
        if (account.HasCode && code is null) throw new InvalidDataException($"Missing PBT bytecode for {account.CodeHash}.");
        ValueHash256 basicData = default;
        PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, (uint)(code?.Code.Length ?? 0), account.Nonce, account.Balance);
        if (code is null || !Eip7702Constants.IsDelegatedCode(code.CodeSpan)) return new(basicData, account.CodeHash.ValueHash256, false);
        ValueHash256 delegation = default;
        code.CodeSpan.CopyTo(delegation.BytesAsSpan);
        return new(basicData, delegation, true);
    }

    /// <summary>The nonce and balance of <paramref name="account"/> over the code of <paramref name="previous"/>, for an unchanged code hash.</summary>
    public static PbtAccount From(in PbtAccount previous, Account account)
    {
        ValueHash256 basicData = default;
        PbtKeyDerivation.PackBasicData(basicData.BytesAsSpan, previous.CodeSize, account.Nonce, account.Balance);
        return previous with { BasicData = basicData };
    }

    public void Encode(Span<byte> destination)
    {
        BasicData.Bytes.CopyTo(destination);
        CodeLeaf.Bytes[..(EncodedLength - LeafLength)].CopyTo(destination[LeafLength..]);
    }

    public static PbtAccount Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is not (LeafLength or LeafLength + DelegationLength or 2 * LeafLength) || encoded[..4].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid PBT account encoding.");
        ValueHash256 basicData = new(encoded[..LeafLength]);
        if (encoded.Length == LeafLength) return new(basicData, Keccak.OfAnEmptyString.ValueHash256, false);
        ValueHash256 codeLeaf = default;
        encoded[LeafLength..].CopyTo(codeLeaf.BytesAsSpan);
        return new(basicData, codeLeaf, encoded.Length == LeafLength + DelegationLength);
    }
}
