// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Blockchain.PartialArchive;

/// <summary>
/// Key/value encodings for the partial archive database columns.
/// </summary>
/// <remarks>
/// Unlike the HalfPath node-storage keys, path keys here contain the full 32-byte path so that
/// distinct paths never collide. Layouts:
/// <list type="bullet">
/// <item>path key (state): <c>0x00 | path(32) | nibbleLength(1)</c> — 34 bytes</item>
/// <item>path key (storage): <c>0x01 | address(32) | path(32) | nibbleLength(1)</c> — 66 bytes</item>
/// <item>latest-version value: <c>keccak(32) | blockNumber(8, BE)</c></item>
/// <item>journal key: <c>blockNumber(8, BE) | pathKey | keccak(32)</c> — big-endian block prefix
/// makes ordered iteration process journals in ascending block order</item>
/// </list>
/// </remarks>
internal static class PartialArchiveKeys
{
    private const byte StateFlag = 0;
    private const byte StorageFlag = 1;

    public const int StatePathKeyLength = 1 + 32 + 1;
    public const int StoragePathKeyLength = 1 + 32 + 32 + 1;
    public const int MaxPathKeyLength = StoragePathKeyLength;
    public const int VersionValueLength = 32 + sizeof(ulong);
    public const int MaxJournalKeyLength = sizeof(ulong) + MaxPathKeyLength + 32;

    public static int WritePathKey(Span<byte> buffer, Hash256? address, in TreePath path)
    {
        int offset = 1;
        if (address is null)
        {
            buffer[0] = StateFlag;
        }
        else
        {
            buffer[0] = StorageFlag;
            address.Bytes.CopyTo(buffer[offset..]);
            offset += 32;
        }

        path.Path.Bytes.CopyTo(buffer[offset..]);
        offset += 32;
        buffer[offset] = (byte)path.Length;
        return offset + 1;
    }

    public static void WriteVersionValue(Span<byte> buffer, Hash256 keccak, ulong blockNumber)
    {
        keccak.Bytes.CopyTo(buffer);
        BinaryPrimitives.WriteUInt64BigEndian(buffer[32..], blockNumber);
    }

    public static (ValueHash256 Keccak, ulong BlockNumber) ReadVersionValue(ReadOnlySpan<byte> value) =>
        (new ValueHash256(value[..32]), BinaryPrimitives.ReadUInt64BigEndian(value[32..]));

    public static ulong ReadJournalBlockNumber(ReadOnlySpan<byte> journalKey) =>
        BinaryPrimitives.ReadUInt64BigEndian(journalKey);

    /// <summary>Superseded-at key: <c>pathKey | keccak(32)</c> — the journal key without its block prefix.</summary>
    public static int WriteSupersededKey(Span<byte> buffer, ReadOnlySpan<byte> pathKey, in ValueHash256 keccak)
    {
        pathKey.CopyTo(buffer);
        keccak.Bytes.CopyTo(buffer[pathKey.Length..]);
        return pathKey.Length + 32;
    }

    public static ReadOnlySpan<byte> JournalKeyToSupersededKey(ReadOnlySpan<byte> journalKey) =>
        journalKey[sizeof(ulong)..];

    public static byte[] BlockNumberValue(ulong blockNumber)
    {
        byte[] buffer = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, blockNumber);
        return buffer;
    }

    public static ulong ReadBlockNumberValue(ReadOnlySpan<byte> value) =>
        BinaryPrimitives.ReadUInt64BigEndian(value);

    public static bool TryParseJournalKey(
        ReadOnlySpan<byte> journalKey,
        out ulong blockNumber,
        out Hash256? address,
        out TreePath path,
        out ValueHash256 keccak)
    {
        blockNumber = 0;
        address = null;
        path = TreePath.Empty;
        keccak = default;

        if (journalKey.Length != sizeof(ulong) + StatePathKeyLength + 32
            && journalKey.Length != sizeof(ulong) + StoragePathKeyLength + 32)
        {
            return false;
        }

        blockNumber = BinaryPrimitives.ReadUInt64BigEndian(journalKey);
        ReadOnlySpan<byte> rest = journalKey[sizeof(ulong)..];

        int offset = 1;
        switch (rest[0])
        {
            case StateFlag:
                if (rest.Length != StatePathKeyLength + 32) return false;
                break;
            case StorageFlag:
                if (rest.Length != StoragePathKeyLength + 32) return false;
                address = new Hash256(rest.Slice(offset, 32));
                offset += 32;
                break;
            default:
                return false;
        }

        int nibbleLength = rest[offset + 32];
        if (nibbleLength > 64) return false;
        path = new TreePath(new ValueHash256(rest.Slice(offset, 32)), nibbleLength);
        keccak = new ValueHash256(rest.Slice(offset + 32 + 1, 32));
        return true;
    }

    public static ReadOnlySpan<byte> JournalPathKey(ReadOnlySpan<byte> journalKey) =>
        journalKey[sizeof(ulong)..^32];
}
