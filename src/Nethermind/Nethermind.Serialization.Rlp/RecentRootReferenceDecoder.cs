// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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

    public void EncodeArray<TWriter>(ref TWriter writer, RecentRootReference[]? items)
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
            Encode(ref writer, items[i]);
        }
    }

    public override int GetLength(RecentRootReference item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item));

    public int GetArrayLength(RecentRootReference[]? items) => items is null ? 1 : Rlp.LengthOfSequence(GetArrayContentLength(items));

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
