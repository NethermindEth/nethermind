// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO.Hashing;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.PersistedSnapshots;

internal static class PbtRetainedKey
{
    internal const byte Address = 0x10;
    internal const byte Code = 0x20;
    internal const byte AccountGroup = 0x30;
    internal const byte CodeGroup = 0x31;
    internal const byte StorageGroup = 0x32;
    internal const byte Ownership = 0xF0;
    internal const byte Clear = 0;
    internal const byte Account = 1;
    internal const byte HeaderRun = 2;
    internal const byte StorageRun = 3;

    internal static ulong BloomHash(ReadOnlySpan<byte> descriptor) => XxHash64.HashToUInt64(descriptor);

    internal static byte[] AddressEntity(in ValueHash256 address, byte tag)
    {
        byte[] key = new byte[35];
        key[0] = Address;
        address.Bytes.CopyTo(key.AsSpan(1));
        key[33] = tag;
        return key;
    }

    internal static byte[] CodeEntity(in ValueHash256 hash)
    {
        byte[] key = new byte[34];
        key[0] = Code;
        hash.Bytes.CopyTo(key.AsSpan(1));
        return key;
    }

    internal static byte[] Run<TKey>(in TKey path) where TKey : struct, IPbtKey<TKey>
    {
        bool header = typeof(TKey) == typeof(PbtPath);
        ReadOnlySpan<byte> bytes = path.Bytes;
        if (bytes.Length != (header ? 34 : 66) || bytes[0] != (header ? Eip8297KeyDerivation.AccountZone : Eip8297KeyDerivation.StorageZone)
            || (bytes[^1] & 15) != 0 || header && bytes[^1] is < 64 or >= 128)
            throw new InvalidDataException("Invalid retained PBT storage run key.");
        byte[] key = new byte[header ? 36 : 68];
        key[0] = Address;
        bytes.Slice(1, 32).CopyTo(key.AsSpan(1));
        key[33] = header ? HeaderRun : StorageRun;
        bytes[33..].CopyTo(key.AsSpan(34));
        return key;
    }

    internal static byte[] Group<TPath>(in TPath path) where TPath : struct, IPbtNodePath<TPath>
    {
        byte family = PbtRocksDbPersistence.PartitionColumn(path) switch
        {
            PbtColumns.CodeNodeGroups => CodeGroup,
            PbtColumns.StorageNodeGroups => StorageGroup,
            _ => AccountGroup,
        };
        byte[] key = new byte[4 + (path.BitDepth + 7) / 8];
        key[0] = family;
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(1), checked((ushort)path.BitDepth));
        PbtNodePathOperations.CopyTo(path, key.AsSpan(3));
        ValidateDescriptor(key);
        return key;
    }

    internal static byte[] Chunk(ReadOnlySpan<byte> descriptor, uint index)
    {
        byte[] key = new byte[descriptor.Length + 4];
        descriptor.CopyTo(key);
        key[descriptor.Length - 1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(descriptor.Length), index);
        return key;
    }

    internal static byte[] Owner(ushort id)
    {
        byte[] key = [Ownership, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(1), id);
        return key;
    }

    internal static void ValidateDescriptor(ReadOnlySpan<byte> key)
    {
        if (key.Length < 2 || key[^1] != 0) throw new InvalidDataException("Invalid retained PBT descriptor key.");
        switch (key[0])
        {
            case Address:
                if (key.Length < 35) throw new InvalidDataException("Invalid retained PBT address key.");
                switch (key[33])
                {
                    case Clear or Account when key.Length == 35: return;
                    case HeaderRun when key.Length == 36 && key[34] is >= 64 and < 128 && (key[34] & 15) == 0: return;
                    case StorageRun when key.Length == 68 && (key[66] & 15) == 0: return;
                    default: throw new InvalidDataException("Invalid retained PBT address subtag or run alignment.");
                }
            case Code when key.Length == 34: return;
            case AccountGroup or CodeGroup or StorageGroup:
                PbtStorageNodePath path = DecodeGroup(key);
                byte family = PbtRocksDbPersistence.PartitionColumn(path) switch
                {
                    PbtColumns.CodeNodeGroups => CodeGroup,
                    PbtColumns.StorageNodeGroups => StorageGroup,
                    _ => AccountGroup,
                };
                if (family != key[0]) throw new InvalidDataException("Retained PBT group partition mismatch.");
                return;
            default: throw new InvalidDataException("Unknown retained PBT entity family.");
        }
    }

    internal static PbtStorageNodePath DecodeGroup(ReadOnlySpan<byte> key)
    {
        if (key.Length < 4) throw new InvalidDataException("Invalid retained PBT group key.");
        int depth = BinaryPrimitives.ReadUInt16BigEndian(key[1..]);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(depth) || depth > (key[0] == StorageGroup ? PbtStorageNodePath.MaxBitDepth : PbtNodePath.MaxBitDepth)
            || key.Length != 4 + (depth + 7) / 8)
            throw new InvalidDataException("Invalid retained PBT group depth.");
        if ((depth & 7) != 0 && (key[^2] & (0xFF >> (depth & 7))) != 0)
            throw new InvalidDataException("Invalid retained PBT group padding.");
        try { return new PbtStorageNodePath(key.Slice(3, key.Length - 4), depth); }
        catch (ArgumentException e) { throw new InvalidDataException("Invalid retained PBT group padding.", e); }
    }

    internal static PbtPath DecodeHeaderRun(ReadOnlySpan<byte> key)
    {
        Span<byte> bytes = stackalloc byte[34];
        bytes[0] = Eip8297KeyDerivation.AccountZone;
        key.Slice(1, 32).CopyTo(bytes[1..]);
        bytes[33] = key[34];
        return new(bytes);
    }

    internal static PbtStoragePath DecodeStorageRun(ReadOnlySpan<byte> key)
    {
        Span<byte> bytes = stackalloc byte[66];
        bytes[0] = Eip8297KeyDerivation.StorageZone;
        key.Slice(1, 32).CopyTo(bytes[1..]);
        key.Slice(34, 33).CopyTo(bytes[33..]);
        return new(bytes);
    }
}
