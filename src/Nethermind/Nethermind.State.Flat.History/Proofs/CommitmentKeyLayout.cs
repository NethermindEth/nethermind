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
    public const int MaxPathLength = 1 + Hash256.Size;
    public const int MaxKeyLength = IdentityLength + MaxPathLength + SuffixLength;
    public const byte ExactRowFlag = 0x80;

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
}
