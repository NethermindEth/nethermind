// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Nethermind.Xdc;
public static class XdcExtensions
{
    //TODO can we wire up this so we can use Rlp.Encode()?
    private static XdcHeaderDecoder _headerDecoder = new();
    private static VoteDecoder _voteDecoder = new();
    public static Signature Sign(this IEthereumEcdsa ecdsa, PrivateKey privateKey, XdcBlockHeader header)
    {
        ValueHash256 hash = ValueKeccak.Compute(_headerDecoder.Encode(header, RlpBehaviors.ForSealing).Bytes);
        return ecdsa.Sign(privateKey, in hash);
    }

    public static Address RecoverVoteSigner(this IEthereumEcdsa ecdsa, Vote vote)
    {
        KeccakRlpStream stream = new();
        //TODO this could be optimized to encoding directly to KeccakRlpStream to avoid several allocation
        _voteDecoder.Encode(stream, vote, RlpBehaviors.ForSealing);
        ValueHash256 hash = stream.GetValueHash();
        return ecdsa.RecoverAddress(vote.Signature, in hash);
    }

    public static IXdcReleaseSpec GetXdcSpec(this ISpecProvider specProvider, XdcBlockHeader xdcBlockHeader, ulong round = 0)
    {
        IXdcReleaseSpec spec = specProvider.GetSpec(xdcBlockHeader) as IXdcReleaseSpec;
        if (spec is null)
            throw new InvalidOperationException($"Expected {nameof(IXdcReleaseSpec)}.");
        spec.ApplyV2Config(round);
        return spec;
    }

    public static Snapshot? GetSnapshotByHeader(this ISnapshotManager snapshotManager, XdcBlockHeader? header)
    {
        if (header is null)
            return null;
        return snapshotManager.GetSnapshot(header.Hash);
    }

    public static Snapshot? GetSnapshotByHeaderNumber(this ISnapshotManager snapshotManager, IBlockTree tree, ulong number, ulong xdcEpoch, ulong xdcGap)
    {
        ulong gapBlockNum = Math.Max(0, number - number % xdcEpoch - xdcGap);

        return snapshotManager.GetSnapshotByGapNumber(tree, gapBlockNum);
    }


    public static Snapshot? GetSnapshotByGapNumber(this ISnapshotManager snapshotManager, IBlockTree tree, ulong gapBlockNum)
    {
        Hash256 gapBlockHash = tree.FindHeader((long)gapBlockNum)?.Hash;

        if (gapBlockHash is null)
            return null;

        return snapshotManager.GetSnapshot(gapBlockHash);
    }
    public static ImmutableArray<Address>? ExtractAddresses(Span<byte> data)
    {
        if (data.Length % Address.Size != 0)
            return null;

        Address[] addresses = new Address[data.Length / Address.Size];
        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i] = new Address(data.Slice(i * Address.Size, Address.Size));
        }
        return addresses.ToImmutableArray();
    }

    public static T[] RemoveItemFromArray<T>(T[] candidates, T[] penalties, int withMaxCap = int.MaxValue)
    {
        if (penalties == null || penalties.Length == 0)
            return candidates; // nothing to remove

        var penaltySet = new HashSet<T>(penalties); // O(penalties.Length)

        // allocate result with upper bound = candidates.Length
        var result = new T[candidates.Length];
        int idx = 0;

        for (int i = 0; i < candidates.Length; i++)
        {
            if (!penaltySet.Contains(candidates[i]))
            {
                result[idx++] = candidates[i];
            }
        }

        if (idx == result.Length)
            return result; // no removals happened

        // trim excess
        Array.Resize(ref result, Math.Min(withMaxCap, idx));
        return result;
    }
}
