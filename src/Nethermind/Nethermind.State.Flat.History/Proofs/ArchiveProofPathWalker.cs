// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

/// <summary>
/// Collects an EIP-1186 proof by following only the key's nibble path, loading each hashed node through
/// <see cref="HistoricalTrieNodeBuilder.LoadRlp"/>, which verifies it against the reference its parent holds.
/// Produces the same proof items as <see cref="AccountProofCollector"/> under a full trie visit: every hashed
/// node on the path in root-to-leaf order, inline nodes omitted.
/// </summary>
internal static class ArchiveProofPathWalker
{
    private const int BranchItems = 17;

    public static bool TryProveAccount(
        AccountProofCollector collector,
        HistoricalTrieNodeBuilder accounts,
        in ValueHash256 stateRoot,
        VisitingStats? diagnostics,
        out AccountStruct account)
    {
        account = default;
        if (!TryWalk(accounts, stateRoot, collector.HashedAddress, collector.AccountProofItems, diagnostics, collector.CancellationToken, out ReadOnlySpan<byte> accountRlp)) return false;

        if (!AccountDecoder.Instance.TryDecodeStruct(accountRlp, out account))
        {
            throw new InvalidDataException("Non storage leaf should be an account");
        }

        collector.SetAccount(account);
        return true;
    }

    public static void ProveSlot(
        AccountProofCollector collector,
        HistoricalTrieNodeBuilder storage,
        in ValueHash256 storageRoot,
        in ValueHash256 slot,
        int index,
        VisitingStats? diagnostics)
    {
        if (TryWalk(storage, storageRoot, slot, collector.StorageProofItems(index), diagnostics, collector.CancellationToken, out ReadOnlySpan<byte> value))
        {
            collector.SetStorageValue(index, new RlpReader(value).DecodeByteArray());
        }
    }

    private static bool TryWalk(
        HistoricalTrieNodeBuilder builder,
        in ValueHash256 root,
        in ValueHash256 key,
        List<byte[]> proof,
        VisitingStats? diagnostics,
        CancellationToken token,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (root == Keccak.EmptyTreeHash.ValueHash256) return false;

        int depth = 0;
        ReadOnlySpan<byte> node = Load(builder, key, depth, root, proof, diagnostics);
        while (true)
        {
            token.ThrowIfCancellationRequested();

            RlpReader reader = new(node);
            int end = reader.ReadSequenceLength() + reader.Position;
            if (reader.PeekNumberOfItemsRemaining(end) == BranchItems)
            {
                reader.SkipItems(NibbleAt(key, depth));
                depth++;
            }
            else
            {
                ReadOnlySpan<byte> hexPrefix = reader.DecodeByteArraySpan();
                bool isLeaf = (hexPrefix[0] & 0x20) != 0;
                if (!MatchesKey(hexPrefix, key, ref depth)) return false;

                if (isLeaf)
                {
                    if (depth != CommitmentDepthPolicy.MaxTrieDepth) return false;

                    value = reader.DecodeByteArraySpan();
                    return true;
                }
            }

            (int prefixLength, int contentLength) = reader.PeekPrefixAndContentLength();
            if (contentLength == 0) return false;

            if (!reader.IsSequenceNext() && contentLength == Hash256.Size)
            {
                reader.SkipBytes(prefixLength);
                node = Load(builder, key, depth, new ValueHash256(reader.Read(Hash256.Size)), proof, diagnostics);
            }
            else
            {
                node = reader.Read(prefixLength + contentLength);
            }
        }
    }

    private static byte[] Load(HistoricalTrieNodeBuilder builder, in ValueHash256 key, int depth, in ValueHash256 hash, List<byte[]> proof, VisitingStats? diagnostics)
    {
        TreePath path = new(key, CommitmentDepthPolicy.MaxTrieDepth);
        path.TruncateMut(depth);
        if (diagnostics is not null)
        {
            diagnostics.RecordLookup();
            diagnostics.RecordCacheMiss();
            diagnostics.ObserveDepth(depth);
        }

        byte[] rlp = builder.LoadRlp(path, hash);
        proof.Add(rlp);
        return rlp;
    }

    private static bool MatchesKey(ReadOnlySpan<byte> hexPrefix, in ValueHash256 key, ref int depth)
    {
        if ((hexPrefix[0] & 0x10) != 0 && !MatchesNibble(hexPrefix[0] & 0x0F, key, ref depth)) return false;

        for (int index = 1; index < hexPrefix.Length; index++)
        {
            if (!MatchesNibble(hexPrefix[index] >> 4, key, ref depth) || !MatchesNibble(hexPrefix[index] & 0x0F, key, ref depth)) return false;
        }

        return true;
    }

    private static bool MatchesNibble(int nibble, in ValueHash256 key, ref int depth) =>
        depth < CommitmentDepthPolicy.MaxTrieDepth && NibbleAt(key, depth++) == nibble;

    private static int NibbleAt(in ValueHash256 key, int depth)
    {
        byte packed = key.Bytes[depth / 2];
        return depth % 2 == 0 ? packed >> 4 : packed & 0x0F;
    }
}
