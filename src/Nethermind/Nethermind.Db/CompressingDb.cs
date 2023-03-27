// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Db;

public static class KeyValueStoreCompressingExtensions
{
    /// <summary>
    /// Applies the compression that optimizes heavily accounts encoded with RLP that are stored in the db.
    /// </summary>
    /// <param name="this"></param>
    /// <returns></returns>
    public static IDb CompressWithAccountAwareCompression(this IDb @this) => new AccountAwareCompressingDb(@this);
}

public class AccountAwareCompressingDb : IDb
{
    private readonly IDb _wrapped;

    public AccountAwareCompressingDb(IDb wrapped)
    {
        // TODO: consider wrapping IDbWithSpan to make the read with a span, with no alloc for reading?
        _wrapped = wrapped;
    }

    public byte[]? this[ReadOnlySpan<byte> key]
    {
        get => Decompress(_wrapped[key]);
        set => _wrapped[key] = Compress(value);
    }

    public IBatch StartBatch() => new Batch(_wrapped.StartBatch());

    private class Batch : IBatch
    {
        private readonly IBatch _wrapped;

        public Batch(IBatch wrapped) => _wrapped = wrapped;

        public void Dispose() => _wrapped.Dispose();

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Decompress(_wrapped[key]);
            set => _wrapped[key] = Compress(value);
        }
    }

    /// <summary>
    /// Taken from <see cref="Rlp"/>
    /// </summary>
    // TODO: make it readonly span initialized inline?
    static readonly byte[] EmptyTreeBytes = Rlp.Encode(Keccak.EmptyTreeHash.Bytes).Bytes;

    static readonly byte[] EmptyStringBytes = Rlp.Encode(Keccak.OfAnEmptyString.Bytes).Bytes;
    private const int CompressedAwayLength = 33;

    /// <summary>
    /// The best estimate of max length at the moment. Maybe can be a bit bigger?
    /// </summary>
    private const int MaxLength = (byte.MaxValue & IndexMask) - IndexShift + CompressedAwayLength;

    // 1 for preamble and 1 for each compressed-away value
    private const int TotalCompressionBytes = SlotCount + 1;

    private const byte PreambleByte = 0;
    private const int SlotCount = 2; // 2 items compressed now
    private const byte PreambleValue = 0;
    private const byte EmptyTreeBytesBit = 0b1000_0000;
    private const byte EmptyStringBytesBit = 0b0000_0000;
    private const byte IndexMask = 0b0111_1111;
    private const byte TypeMask = 0b1000_0000;

    /// <summary>
    /// Index shift is used to make index of 0, non-zero, so that checking for existence can be made with 0.
    /// </summary>
    private const byte IndexShift = 1;

    /// <summary>
    /// Represents <see cref="MemoryExtensions.IndexOf"/> not found entry.
    /// </summary>
    private const int NotFound = -1;

    private static byte[]? Compress(byte[]? bytes)
    {
        if (bytes == null)
            return bytes;

        // 1 byte addressing for compressed-away values so drop
        // at the same time accounts should be not bigger than 100 bytes so that should be fine
        if (bytes.Length >= MaxLength)
            return bytes;

        Span<byte> span = bytes.AsSpan();
        int emptyTree = span.IndexOf(EmptyTreeBytes);
        int emptyString = span.IndexOf(EmptyStringBytes);

        if (emptyTree == NotFound && emptyString == NotFound)
        {
            // nothing to compress here, just return bytes
            return bytes;
        }

        // compression is possible, write preamble
        int length = bytes.Length + TotalCompressionBytes;

        if (emptyTree != NotFound)
        {
            length -= CompressedAwayLength;
        }

        if (emptyString != NotFound)
        {
            length -= CompressedAwayLength;
        }

        byte[] result = new byte[length];
        Span<byte> compressed = result.AsSpan();

        compressed[PreambleByte] = PreambleValue;

        // at least one compressed entry is present
        if (emptyString == NotFound)
        {
            compressed[1] = (byte)(emptyTree + IndexShift | EmptyTreeBytesBit);
            CopyOmittingCompressed(span, emptyTree, compressed);
        }
        else if (emptyTree == NotFound)
        {
            compressed[1] = (byte)(emptyString + IndexShift | EmptyStringBytesBit);
            CopyOmittingCompressed(span, emptyString, compressed);
        }
        else
        {
            int index0, index1;

            // both exist, order them
            if (emptyString < emptyTree)
            {
                compressed[1] = (byte)(emptyString + IndexShift | EmptyStringBytesBit);
                compressed[2] = (byte)(emptyTree + IndexShift | EmptyTreeBytesBit);
                index0 = emptyString;
                index1 = emptyTree;
            }
            else
            {
                compressed[1] = (byte)(emptyTree + IndexShift | EmptyTreeBytesBit);
                compressed[2] = (byte)(emptyString + IndexShift | EmptyStringBytesBit);
                index0 = emptyTree;
                index1 = emptyString;
            }

            // copy first part, to index0
            span.Slice(0, index0).CopyTo(compressed.Slice(TotalCompressionBytes));

            // copy the middle part, between index0 + CompressedAwayLength and index1
            int middleStart = index0 + CompressedAwayLength;
            span.Slice(middleStart, index1 - middleStart)
                .CopyTo(compressed.Slice(TotalCompressionBytes + index0));

            // copy the last part
            span.Slice(index1 + CompressedAwayLength)
                .CopyTo(compressed.Slice(TotalCompressionBytes + index1 - CompressedAwayLength));
        }

        return result;

        static void CopyOmittingCompressed(in Span<byte> original, int index, in Span<byte> compressed)
        {
            // copy first part
            original.Slice(0, index).CopyTo(compressed.Slice(TotalCompressionBytes));

            // copy the second omitting the string
            original.Slice(index + CompressedAwayLength).CopyTo(compressed.Slice(TotalCompressionBytes + index));
        }
    }

    private static byte[]? Decompress(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0 || (bytes[PreambleByte] != PreambleValue))
        {
            return bytes;
        }

        // there's the compression preamble, find how many are needed
        int compressedAwayCount = ((bytes[PreambleByte + 1] != 0) ? 1 : 0) +
                                  ((bytes[PreambleByte + 2] != 0) ? 1 : 0);

        int length = bytes.Length - TotalCompressionBytes + compressedAwayCount * CompressedAwayLength;

        Span<byte> source = bytes.AsSpan(TotalCompressionBytes);
        byte[] result = new byte[length];
        Span<byte> destination = result.AsSpan();

        if (compressedAwayCount == 1)
        {
            int index = (IndexMask & bytes[PreambleByte + 1]) - IndexShift;

            // only 1, find which case
            // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
            if ((bytes[PreambleByte + 1] & TypeMask) == EmptyTreeBytesBit)
            {
                // empty tree case
                CopyWithDecompressionOfOne(source, index, destination, EmptyTreeBytes);
            }
            else
            {
                // empty string case
                CopyWithDecompressionOfOne(source, index, destination, EmptyStringBytes);
            }
        }
        else
        {
            byte[] compressedAway0 = (bytes[PreambleByte + 1] & TypeMask) == EmptyStringBytesBit
                ? EmptyStringBytes
                : EmptyTreeBytes;
            int index0 = (IndexMask & bytes[PreambleByte + 1]) - IndexShift;

            byte[] compressedAway1 = (bytes[PreambleByte + 2] & TypeMask) == EmptyStringBytesBit
                ? EmptyStringBytes
                : EmptyTreeBytes;
            int index1 = (IndexMask & bytes[PreambleByte + 2]) - IndexShift;

            source.Slice(0, index0).CopyTo(destination);
            compressedAway0.CopyTo(destination.Slice(index0));
            source.Slice(index0, index1 - index0 - CompressedAwayLength).CopyTo(destination.Slice(index0 + CompressedAwayLength));
            compressedAway1.CopyTo(destination.Slice(index1));
            source.Slice(index1 - CompressedAwayLength).CopyTo(destination.Slice(index1 + CompressedAwayLength));
        }

        return result;

        static void CopyWithDecompressionOfOne(in Span<byte> span, int at, in Span<byte> destination,
            ReadOnlySpan<byte> bytesCompressedAway)
        {
            span.Slice(0, at).CopyTo(destination);
            bytesCompressedAway.CopyTo(destination.Slice(at));
            span.Slice(at).CopyTo(destination.Slice(at + CompressedAwayLength));
        }
    }

    public void Dispose() => _wrapped.Dispose();

    public string Name => _wrapped.Name;

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => throw new NotImplementedException();

    public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => _wrapped.GetAll(ordered)
        .Select(kvp => new KeyValuePair<byte[], byte[]>(kvp.Key, Decompress(kvp.Value)));

    public IEnumerable<byte[]> GetAllValues(bool ordered = false) => _wrapped.GetAllValues(ordered).Select(Decompress);

    public void Remove(ReadOnlySpan<byte> key) => _wrapped.Remove(key);

    public bool KeyExists(ReadOnlySpan<byte> key) => _wrapped.KeyExists(key);

    public void Flush() => _wrapped.Flush();

    public void Clear() => _wrapped.Clear();
}
