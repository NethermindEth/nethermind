// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

/// <summary>Decodes the EIP-8272 recent-root reference tuple <c>[source_id, slot, root]</c>.</summary>
public sealed class RecentRootReferenceDecoder : RlpDecoder<RecentRootReference>
{
    public static readonly RecentRootReferenceDecoder Instance = new();

    protected override RecentRootReference DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;

        ValueHash256 sourceId = decoderContext.DecodeValueKeccak() ?? ThrowMissingHash();
        ulong slot = decoderContext.DecodeULong();
        ValueHash256 root = decoderContext.DecodeValueKeccak() ?? ThrowMissingHash();

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            decoderContext.Check(check);
        }

        return new RecentRootReference(sourceId, slot, root);
    }

    public override void Encode<TWriter>(ref TWriter writer, RecentRootReference item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        writer.StartSequence(GetContentLength(item));
        writer.Encode(item.SourceId);
        writer.Encode(item.Slot);
        writer.Encode(item.Root);
    }

    public void EncodeArray<TWriter>(ref TWriter writer, RecentRootReference[] items)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(GetArrayContentLength(items));
        for (int i = 0; i < items.Length; i++)
        {
            Encode(ref writer, items[i]);
        }
    }

    public override int GetLength(RecentRootReference item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item));

    public int GetArrayLength(RecentRootReference[] items) => Rlp.LengthOfSequence(GetArrayContentLength(items));

    /// <summary>Zero and non-zero byte counts of the encoded reference array, measured off the encoding so the
    /// EIP-8141 calldata charge cannot drift from the wire form.</summary>
    public (int ZeroBytes, int NonZeroBytes) Measure(RecentRootReference[]? references)
    {
        if (references is null)
        {
            return (0, 0);
        }

        int length = GetArrayLength(references);
        // A dynamic stackalloc turns an out-of-range length into an uncatchable stack overflow, and this is
        // public: the references can reach it from an RPC-built transaction the decoder never bounded.
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxCalldataLength);
        Span<byte> calldata = stackalloc byte[length];
        RlpWriter writer = new(calldata);
        EncodeArray(ref writer, references);
        int zeros = calldata.CountZeros();
        return (zeros, length - zeros);
    }

    /// <summary>Upper bound on <c>recent_root_calldata</c>: a full set of references, each a 32-byte source id
    /// and root plus a nine-byte slot behind a two-byte long-form tuple header, behind a three-byte array header.</summary>
    private const int MaxCalldataLength = 3 + Eip8272Constants.MaxRecentRootReferences * (2 + 33 + 9 + 33);

    private int GetArrayContentLength(RecentRootReference[] items)
    {
        int length = 0;
        for (int i = 0; i < items.Length; i++)
        {
            length += GetLength(items[i], RlpBehaviors.None);
        }

        return length;
    }

    [DoesNotReturn, StackTraceHidden]
    private static ValueHash256 ThrowMissingHash() => throw new RlpException("recent root reference source id and root must be 32-byte hashes");

    private static int GetContentLength(RecentRootReference item) =>
        Rlp.LengthOf(item.SourceId)
        + Rlp.LengthOf(item.Slot)
        + Rlp.LengthOf(item.Root);
}
