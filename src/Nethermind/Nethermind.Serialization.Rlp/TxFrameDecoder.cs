// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Decodes the EIP-8141 frame tuple <c>[mode, flags, target, limits, value, data]</c>, where
/// <c>limits = [execution, state]</c>.
/// An empty target byte string decodes to null (resolves to the transaction sender).
/// </summary>
public sealed class TxFrameDecoder : RlpDecoder<TxFrame>
{
    public static readonly TxFrameDecoder Instance = new();

    // EIP8141-GAP: the spec does not bound individual frame data size; mirrors the base tx data cap.
    private static readonly RlpLimit _dataRlpLimit = RlpLimit.For<TxFrame>((int)30.MiB, nameof(TxFrame.Data));

    protected override TxFrame DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;

        byte mode = decoderContext.DecodeByte();
        byte flags = decoderContext.DecodeByte();
        Address? target = decoderContext.DecodeAddressOrNull();

        int limitsLength = decoderContext.ReadSequenceLength();
        int limitsCheck = limitsLength + decoderContext.Position;
        ulong executionGasLimit = decoderContext.DecodeULong();
        ulong stateGasLimit = decoderContext.DecodeULong();
        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            decoderContext.Check(limitsCheck);
        }

        UInt256 value = decoderContext.DecodeUInt256();
        ReadOnlyMemory<byte> data = decoderContext.DecodeByteArrayMemory(_dataRlpLimit);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            decoderContext.Check(check);
        }

        return new TxFrame(mode, flags, target, executionGasLimit, stateGasLimit, value, data);
    }

    public override void Encode<TWriter>(ref TWriter writer, TxFrame item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        writer.StartSequence(GetContentLength(item));
        writer.Encode((ulong)item.Mode);
        writer.Encode((ulong)item.Flags);
        writer.Encode(item.Target);
        writer.StartSequence(GetLimitsContentLength(item));
        writer.Encode(item.ExecutionGasLimit);
        writer.Encode(item.StateGasLimit);
        writer.Encode(item.Value);
        writer.Encode(item.Data);
    }

    public void EncodeArray<TWriter>(ref TWriter writer, TxFrame[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (items is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        writer.StartSequence(GetArrayContentLength(items));
        for (int i = 0; i < items.Length; i++)
        {
            Encode(ref writer, items[i], rlpBehaviors);
        }
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
