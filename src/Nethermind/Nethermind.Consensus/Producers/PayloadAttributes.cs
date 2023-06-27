// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.Consensus.Producers;

public class PayloadAttributes
{
    public ulong Timestamp { get; set; }

    public Keccak PrevRandao { get; set; }

    public Address SuggestedFeeRecipient { get; set; }

    public IList<Withdrawal>? Withdrawals { get; set; }

    public virtual long? GetGasLimit() => null;

    public override string ToString() => ToString(string.Empty);

    public string ToString(string indentation)
    {
        var sb = new StringBuilder($"{indentation}{nameof(PayloadAttributes)} {{")
            .Append($"{nameof(Timestamp)}: {Timestamp}, ")
            .Append($"{nameof(PrevRandao)}: {PrevRandao}, ")
            .Append($"{nameof(SuggestedFeeRecipient)}: {SuggestedFeeRecipient}");

        if (Withdrawals is not null)
        {
            sb.Append($", {nameof(Withdrawals)} count: {Withdrawals.Count}");
        }

        sb.Append('}');

        return sb.ToString();
    }


    private string? _payloadId;

    public string GetPayloadId(BlockHeader parentHeader) => _payloadId ??= ComputePayloadId(parentHeader);

    [SkipLocalsInit]
    protected virtual string ComputePayloadId(BlockHeader parentHeader)
    {
        Span<byte> inputSpan = stackalloc byte[
            Keccak.Size + // parent hash
            sizeof(long) + // timestamp
            Keccak.Size + // prev randao
            Address.Size + // suggested fee recipient
            Keccak.Size]; // withdrawals root hash

        WritePayloadIdMembers(parentHeader, inputSpan);
        return ComputePayloadId(inputSpan);
    }

    protected static string ComputePayloadId(Span<byte> inputSpan)
    {
        ValueKeccak inputHash = ValueKeccak.Compute(inputSpan);
        return inputHash.BytesAsSpan[..8].ToHexString(true);
    }

    protected virtual int WritePayloadIdMembers(BlockHeader parentHeader, Span<byte> inputSpan)
    {
        parentHeader.Hash!.Bytes.CopyTo(inputSpan.Slice(0, Keccak.Size));
        BinaryPrimitives.WriteUInt64BigEndian(inputSpan.Slice(Keccak.Size, sizeof(ulong)), Timestamp);
        PrevRandao.Bytes.CopyTo(inputSpan.Slice(Keccak.Size + sizeof(ulong), Keccak.Size));
        SuggestedFeeRecipient.Bytes.CopyTo(inputSpan.Slice(Keccak.Size + sizeof(ulong) + Keccak.Size, Address.Size));
        Keccak withdrawalsRootHash = (Withdrawals?.Count ?? 0) == 0 ? PatriciaTree.EmptyTreeHash : new WithdrawalTrie(Withdrawals).RootHash;
        withdrawalsRootHash.Bytes.CopyTo(inputSpan.Slice(Keccak.Size + sizeof(ulong) + Keccak.Size + Address.Size, Keccak.Size));
        return Keccak.Size + sizeof(ulong) + Keccak.Size + Address.Size + Keccak.Size;
    }
}

public static class PayloadAttributesExtensions
{
    public static int GetVersion(this PayloadAttributes executionPayload) =>
        executionPayload.Withdrawals is null ? 1 : 2;

    public static bool Validate(
        this PayloadAttributes payloadAttributes,
        IReleaseSpec spec,
        int version,
        [NotNullWhen(false)] out string? error)
    {
        int actualVersion = payloadAttributes.GetVersion();

        error = actualVersion switch
        {
            1 when spec.WithdrawalsEnabled => "PayloadAttributesV2 expected",
            > 1 when !spec.WithdrawalsEnabled => "PayloadAttributesV1 expected",
            _ => actualVersion > version ? $"PayloadAttributesV{version} expected" : null
        };

        return error is null;
    }

    public static bool Validate(this PayloadAttributes payloadAttributes,
        ISpecProvider specProvider,
        int version,
        [NotNullWhen(false)] out string? error) =>
        payloadAttributes.Validate(
            specProvider.GetSpec(ForkActivation.TimestampOnly(payloadAttributes.Timestamp)),
            version,
            out error);
}
