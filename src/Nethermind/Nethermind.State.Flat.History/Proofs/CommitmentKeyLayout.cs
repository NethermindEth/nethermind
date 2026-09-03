// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

internal static class CommitmentKeyLayout
{
    public const int IdentityLength = BaseFlatPersistence.AccountKeyLength;
    public const int SuffixLength = sizeof(ulong);
    public const int EpochLength = sizeof(ushort);
    public const int TierLength = 1;
    public const int MaxPathLength = 1 + Hash256.Size;
    public const int MaxPrefixLength = IdentityLength + MaxPathLength;
    public const int MaxKeyLength = EpochLength + TierLength + MaxPrefixLength + SuffixLength;
    public const byte ExactRowFlag = 0x80;
    public const byte FineTier = 0x00;
    public const byte ReservedMarker = 0xFF;
    public const ulong MaxEpoch = ushort.MaxValue - 1;

    public static int PathBytes(int depth) => (depth + 1) / 2;

    public static int WritePathPrefix(Span<byte> destination, in TreePath path, bool exact)
    {
        int pathBytes = PathBytes(path.Length);
        destination[0] = (byte)(path.Length | (exact ? ExactRowFlag : 0));
        path.Path.Bytes[..pathBytes].CopyTo(destination[1..]);
        return 1 + pathBytes;
    }

    public static int WriteScopedPathPrefix(Span<byte> destination, scoped ReadOnlySpan<byte> identity, in TreePath path, bool exact)
    {
        identity.CopyTo(destination);
        return identity.Length + WritePathPrefix(destination[identity.Length..], path, exact);
    }

    public static bool IsExactPrefix(scoped ReadOnlySpan<byte> prefix, int identityLength) => (prefix[identityLength] & ExactRowFlag) != 0;

    public static int WriteRowKey(Span<byte> destination, ulong epoch, byte tier, scoped ReadOnlySpan<byte> prefix, ulong suffix)
    {
        int position = WriteEpochTier(destination, epoch, tier);
        prefix.CopyTo(destination[position..]);
        position += prefix.Length;
        BinaryPrimitives.WriteUInt64BigEndian(destination[position..], ~suffix);
        return position + SuffixLength;
    }

    public static int WriteRowUpperBound(Span<byte> destination, ulong epoch, byte tier, scoped ReadOnlySpan<byte> prefix)
    {
        int position = WriteEpochTier(destination, epoch, tier);
        prefix.CopyTo(destination[position..]);
        position += prefix.Length;
        destination.Slice(position, SuffixLength).Fill(0xFF);
        destination[position + SuffixLength] = 0x00;
        return position + SuffixLength + 1;
    }

    public static int WriteEpochTier(Span<byte> destination, ulong epoch, byte tier)
    {
        if (epoch > MaxEpoch) throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "A commitment epoch must fit the two-byte key prefix.");

        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)epoch);
        destination[EpochLength] = tier;
        return EpochLength + TierLength;
    }

    public static int WriteSeekKey(Span<byte> destination, scoped ReadOnlySpan<byte> prefix, ulong suffix)
    {
        prefix.CopyTo(destination);
        BinaryPrimitives.WriteUInt64BigEndian(destination[prefix.Length..], ~suffix);
        return prefix.Length + SuffixLength;
    }

    public static int WriteUpperBound(Span<byte> destination, scoped ReadOnlySpan<byte> prefix)
    {
        prefix.CopyTo(destination);
        destination.Slice(prefix.Length, SuffixLength).Fill(0xFF);
        destination[prefix.Length + SuffixLength] = 0x00;
        return prefix.Length + SuffixLength + 1;
    }

    public static ulong ReadSuffix(scoped ReadOnlySpan<byte> rowKey) =>
        ~BinaryPrimitives.ReadUInt64BigEndian(rowKey[^SuffixLength..]);

    public static void WriteIdentity(Span<byte> destination, in ValueHash256 accountPath) =>
        accountPath.Bytes[..IdentityLength].CopyTo(destination);

    public const int StorageTrieDepthKeyLength = 1 + IdentityLength;

    public static void WriteStorageTrieDepthKey(Span<byte> destination, in ValueHash256 accountPath)
    {
        destination[0] = ReservedMarker;
        WriteIdentity(destination[1..], accountPath);
    }
}
