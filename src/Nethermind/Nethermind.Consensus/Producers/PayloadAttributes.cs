// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>Gets or sets the gas limit.</summary>
    /// <remarks>Used for MEV-Boost only.</remarks>
    public long? GasLimit { get; set; }

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
}

public static class PayloadAttributesExtensions
{
    public static string ComputePayloadId(this PayloadAttributes payloadAttributes, BlockHeader parentHeader)
    {
        bool hasWithdrawals = payloadAttributes.Withdrawals is not null;
        Span<byte> inputSpan = stackalloc byte[32 + 32 + 32 + 20 + (hasWithdrawals ? 32 : 0)];

        parentHeader.Hash!.Bytes.CopyTo(inputSpan[..32]);
        BinaryPrimitives.WriteUInt64BigEndian(inputSpan.Slice(56, 8), payloadAttributes.Timestamp);
        payloadAttributes.PrevRandao.Bytes.CopyTo(inputSpan.Slice(64, 32));
        payloadAttributes.SuggestedFeeRecipient.Bytes.CopyTo(inputSpan.Slice(96, 20));

        if (hasWithdrawals)
        {
            var withdrawalsRootHash = payloadAttributes.Withdrawals.Count == 0
                ? PatriciaTree.EmptyTreeHash
                : new WithdrawalTrie(payloadAttributes.Withdrawals).RootHash;

            withdrawalsRootHash.Bytes.CopyTo(inputSpan[116..]);
        }

        ValueKeccak inputHash = ValueKeccak.Compute(inputSpan);

        return inputHash.BytesAsSpan[..8].ToHexString(true);
    }

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
