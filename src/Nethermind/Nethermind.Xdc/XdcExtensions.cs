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
using System.Linq;

namespace Nethermind.Xdc;
public static class XdcExtensions
{
    //TODO can we wire up this so we can use Rlp.Encode()?
    private static XdcHeaderDecoder _headerDecoder = new();
    public static Signature Sign(this IEthereumEcdsa ecdsa, PrivateKey privateKey, XdcBlockHeader header)
    {
        ValueHash256 hash = ValueKeccak.Compute(_headerDecoder.Encode(header, RlpBehaviors.ForSealing).Bytes);
        return ecdsa.Sign(privateKey, in hash);
    }

    //TODO round parameter is given a default value since this function is being used in places where the current round is not known
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



}
