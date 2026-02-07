// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Immutable;

namespace Nethermind.Xdc;

internal static partial class XdcExtensions
{
    //TODO can we wire up this so we can use Rlp.Encode()?
    private static readonly XdcHeaderDecoder _headerDecoder = new();
    private static readonly VoteDecoder _voteDecoder = new();
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

    public static IXdcReleaseSpec GetXdcSpec(this ISpecProvider specProvider, long blockNumber, ulong round = 0)
    {
        IXdcReleaseSpec spec = specProvider.GetSpec(blockNumber, null) as IXdcReleaseSpec;
        if (spec is null)
            throw new InvalidOperationException($"Expected {nameof(IXdcReleaseSpec)}.");
        spec.ApplyV2Config(round);
        return spec;
    }
    public static Address[]? ExtractAddresses(this Span<byte> data)
    {
        if (data.Length % Address.Size != 0)
            return null;

        Address[] addresses = new Address[data.Length / Address.Size];
        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i] = new Address(data.Slice(i * Address.Size, Address.Size));
        }
        return addresses;
    }

    public static bool ValidateBlockInfo(this BlockRoundInfo blockInfo, XdcBlockHeader blockHeader) =>
        (blockInfo.BlockNumber == blockHeader.Number)
        && (blockInfo.Hash == blockHeader.Hash)
        && (blockInfo.Round == blockHeader.ExtraConsensusData.BlockRound);
}
