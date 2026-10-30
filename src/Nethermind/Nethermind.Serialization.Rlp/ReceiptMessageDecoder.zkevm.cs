// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

/// <remarks>
/// The guest encodes every receipt of a block for its receipts root. The generic encoder hands every field to the
/// writer on its own, so the guest writes the receipt straight into the writer's buffer from the lengths populated
/// for it. Each write returns the reference past the bytes it wrote, which stays in a register where a position held
/// by reference would not.
/// </remarks>
public sealed partial class ReceiptMessageDecoder
{
    partial void EncodeInOnePass<TWriter>(ref TWriter writer, TxReceipt item, ref LengthList lengths, RlpBehaviors rlpBehaviors, ref bool encoded)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (typeof(TWriter) != typeof(RlpWriter) || !IsOnePass(item, rlpBehaviors)) return;

        int contentLength = lengths.Read();
        int length = (item.TxType == TxType.Legacy ? 0 : 1) + Rlp.LengthOfSequence(contentLength);
        ref byte destination = ref MemoryMarshal.GetReference(Unsafe.As<TWriter, RlpWriter>(ref writer).Reserve(length));

        if (item.TxType != TxType.Legacy) destination = ref WriteByte(ref destination, (byte)item.TxType);
        destination = ref WriteSequencePrefix(ref destination, contentLength);
        destination = ref WriteStatus(ref destination, item.StatusCode);
        destination = ref WriteUInt64(ref destination, item.GasUsedTotal);
        destination = ref WriteBloom(ref destination, GetBloom(item, rlpBehaviors));
        destination = ref WriteSequencePrefix(ref destination, lengths.Read());
        foreach (LogEntry log in item.Logs!)
            destination = ref WriteLog(ref destination, log);

        encoded = true;
    }

    /// <summary>Whether the receipt takes the one-pass encoding: an EIP-658 receipt of a transaction other than a frame one, unwrapped.</summary>
    private static bool IsOnePass(TxReceipt item, RlpBehaviors rlpBehaviors) =>
        item.TxType != TxType.FrameTx && item.Logs is not null &&
        (rlpBehaviors & RlpBehaviors.Eip658Receipts) != 0 &&
        (item.TxType == TxType.Legacy || (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) != 0);

    private static ref byte WriteLog(ref byte destination, LogEntry? log)
    {
        if (log is null) return ref WriteByte(ref destination, 0xc0);

        destination = ref WriteSequencePrefix(ref destination, LogEntryDecoder.GetContentLength(log));
        Address? address = log.Address;
        if (address is null)
        {
            destination = ref WriteByte(ref destination, 0x80);
        }
        else
        {
            destination = ref WriteByte(ref destination, 0x80 + Address.Size);
            destination = ref WriteBytes(ref destination, address.Bytes);
        }

        Hash256[] topics = log.Topics;
        destination = ref WriteSequencePrefix(ref destination, topics.Length * Rlp.LengthOfKeccakRlp);
        foreach (Hash256 topic in topics)
        {
            destination = ref WriteByte(ref destination, 0x80 + Hash256.Size);
            destination = ref WriteBytes(ref destination, topic.Bytes);
        }

        return ref WriteByteString(ref destination, log.Data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte WriteBloom(ref byte destination, Bloom? bloom)
    {
        if (bloom is null || bloom.IsZeroLength) return ref WriteByte(ref destination, 0x80);

        // 0xb9 and a two-byte length of 256.
        destination = 0xb9;
        Unsafe.Add(ref destination, 1) = 1;
        Unsafe.Add(ref destination, 2) = 0;
        return ref WriteBytes(ref Unsafe.Add(ref destination, 3), bloom.Bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte WriteStatus(ref byte destination, byte status)
    {
        if (status == 0) return ref WriteByte(ref destination, 0x80);
        if (status >= 0x80) destination = ref WriteByte(ref destination, 0x81);
        return ref WriteByte(ref destination, status);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte WriteUInt64(ref byte destination, ulong value)
    {
        if (value < 0x80) return ref WriteByte(ref destination, value == 0 ? (byte)0x80 : (byte)value);

        int length = sizeof(ulong) - Core.Extensions.Bytes.LeadingZeroBytes(value);
        destination = ref WriteByte(ref destination, (byte)(0x80 + length));
        return ref WriteBigEndian(ref destination, value, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte WriteByteString(ref byte destination, byte[] data)
    {
        int length = data.Length;
        if (length == 1 && data[0] < 0x80) return ref WriteByte(ref destination, data[0]);

        if (length < RlpHelpers.SmallPrefixBarrier)
        {
            destination = ref WriteByte(ref destination, (byte)(0x80 + length));
        }
        else
        {
            int lengthOfLength = Rlp.LengthOfLength(length);
            destination = ref WriteByte(ref destination, (byte)(0xb7 + lengthOfLength));
            destination = ref WriteBigEndian(ref destination, (ulong)length, lengthOfLength);
        }

        return ref WriteBytes(ref destination, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte WriteSequencePrefix(ref byte destination, int contentLength)
    {
        if (contentLength < RlpHelpers.SmallPrefixBarrier) return ref WriteByte(ref destination, (byte)(0xc0 + contentLength));

        int lengthOfLength = Rlp.LengthOfLength(contentLength);
        destination = ref WriteByte(ref destination, (byte)(0xf7 + lengthOfLength));
        return ref WriteBigEndian(ref destination, (ulong)contentLength, lengthOfLength);
    }

    /// <summary>Writes the low <paramref name="length"/> bytes of <paramref name="value"/>, most significant first.</summary>
    private static ref byte WriteBigEndian(ref byte destination, ulong value, int length)
    {
        for (int i = length - 1; i >= 0; i--)
            destination = ref WriteByte(ref destination, (byte)(value >> (8 * i)));
        return ref destination;
    }

    /// <remarks>Through memmove, which moves whole words where a fixed-size copy to an unaligned destination may move bytes.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte WriteBytes(ref byte destination, ReadOnlySpan<byte> source)
    {
        source.CopyTo(MemoryMarshal.CreateSpan(ref destination, source.Length));
        return ref Unsafe.Add(ref destination, source.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte WriteByte(ref byte destination, byte value)
    {
        destination = value;
        return ref Unsafe.Add(ref destination, 1);
    }
}
