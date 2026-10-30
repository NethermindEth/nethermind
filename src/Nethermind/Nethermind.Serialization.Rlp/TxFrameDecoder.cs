// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Decodes the EIP-8141 frame tuple <c>[mode, flags, target, limits, value, data]</c>, where
/// <c>limits = [execution, state]</c>.
/// An empty target byte string decodes to null (resolves to the transaction sender).
/// </summary>
public sealed class TxFrameDecoder : RlpDecoder<TxFrame>
{
    public static readonly TxFrameDecoder Instance = new();

    // EIP8141-DEVIATION: the spec bounds frame data only through gas, which is charged after
    // decoding; this mirrors the base tx data cap to guard the allocation.
    private static readonly RlpLimit _dataRlpLimit = RlpLimit.For<TxFrame>((int)30.MiB, nameof(TxFrame.Data));

    protected override TxFrame DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
            ? Decode<OffFlag>(ref reader, Memory<byte>.Empty, rlpBehaviors)
            : Decode<OnFlag>(ref reader, Memory<byte>.Empty, rlpBehaviors);

    /// <param name="source">The memory that holds the bytes of <paramref name="reader"/>, so that the data refers to it; empty to copy it.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static TxFrame Decode<TCanonicalChecks>(ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int itemLength);

        byte mode = fields.DecodeByte<TCanonicalChecks>();
        byte flags = fields.DecodeByte<TCanonicalChecks>();
        Address? target = fields.DecodeAddressOrNull();

        RlpReader limits = fields.EnterSequence<TCanonicalChecks>(out int limitsLength);
        ulong executionGasLimit = limits.DecodeULong<TCanonicalChecks>();
        ulong stateGasLimit = limits.DecodeULong<TCanonicalChecks>();
        fields.ExitSequence(in limits, limitsLength);

        UInt256 value = fields.DecodeUInt256<TCanonicalChecks>();
        ReadOnlyMemory<byte> data = fields.DecodeByteArrayMemory<TCanonicalChecks>(source, _dataRlpLimit);

        reader.ExitSequence(in fields, itemLength, rlpBehaviors);

        // Unchecked on purpose: an out-of-range mode or a reserved flag bit from a peer is rejected by
        // FrameTxValidation.IsWellFormed, not here, so the decode stays a pure structural read.
        return new TxFrame((FrameMode)mode, (FrameFlags)flags, target, executionGasLimit, stateGasLimit, value, data);
    }

    // A root of its own, so that the writes of each frame keep their inline budget.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void Encode(ref RlpWriter writer, TxFrame item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpSequence sequence = writer.StartSequence(GetContentLength(item));
        writer.Encode((ulong)item.Mode);
        writer.Encode((ulong)item.Flags);
        writer.Encode(item.Target);
        RlpSequence limits = writer.StartSequence(GetLimitsContentLength(item));
        writer.Encode(item.ExecutionGasLimit);
        writer.Encode(item.StateGasLimit);
        writer.EndSequence(limits);
        writer.Encode(item.Value);
        writer.Encode(item.Data);
        writer.EndSequence(sequence);
    }

    public void EncodeArray(ref RlpWriter writer, TxFrame[]? items)
    {
        if (items is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(GetArrayContentLength(items));
        for (int i = 0; i < items.Length; i++)
        {
            Encode(ref writer, items[i]);
        }

        writer.EndSequence(sequence);
    }

    public override int GetLength(TxFrame item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item));

    public int GetArrayLength(TxFrame[]? items) => items is null ? 1 : Rlp.LengthOfSequence(GetArrayContentLength(items));

    private int GetArrayContentLength(TxFrame[] items)
    {
        int length = 0;
        for (int i = 0; i < items.Length; i++)
        {
            length += GetLength(items[i], RlpBehaviors.None);
        }

        return length;
    }

    private static int GetContentLength(TxFrame item) =>
        Rlp.LengthOf((ulong)item.Mode)
        + Rlp.LengthOf((ulong)item.Flags)
        + Rlp.LengthOf(item.Target)
        + Rlp.LengthOfSequence(GetLimitsContentLength(item))
        + Rlp.LengthOf(item.Value)
        + Rlp.LengthOf(item.Data);

    private static int GetLimitsContentLength(TxFrame item) =>
        Rlp.LengthOf(item.ExecutionGasLimit) + Rlp.LengthOf(item.StateGasLimit);
}
