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
using System.Collections.Generic;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc;

internal static partial class XdcExtensions
{
    //TODO can we wire up this so we can use Rlp.Encode()?
    private static readonly XdcHeaderDecoder _headerDecoder = new();
    private static readonly VoteDecoder _voteDecoder = new();

    public static Signature Sign(this IEthereumEcdsa ecdsa, PrivateKey privateKey, XdcBlockHeader header)
    {
        KeccakRlpWriter writer = new();
        _headerDecoder.Encode(ref writer, header, RlpBehaviors.ForSealing);
        ValueHash256 hash = writer.GetValueHash();
        return ecdsa.Sign(privateKey, in hash);
    }

    public static Address? RecoverVoteSigner(this IEthereumEcdsa ecdsa, Vote vote)
    {
        if (vote.Signature is null)
        {
            return null;
        }

        KeccakRlpWriter writer = new();
        _voteDecoder.Encode(ref writer, vote, RlpBehaviors.ForSealing);
        ValueHash256 hash = writer.GetValueHash();
        return ecdsa.RecoverAddress(vote.Signature, in hash);
    }

    public static IXdcReleaseSpec GetXdcSpec(this ISpecProvider specProvider, XdcBlockHeader xdcBlockHeader, ulong round = 0)
    {
        if (specProvider is XdcChainSpecBasedSpecProvider xdcProvider)
            return xdcProvider.GetXdcSpec(xdcBlockHeader, round);
        if (round == 0)
            round = xdcBlockHeader.ExtraConsensusData?.BlockRound ?? 0;
        return specProvider.GetXdcSpec(xdcBlockHeader.Number, round);
    }

    public static IXdcReleaseSpec GetXdcSpec(this ISpecProvider specProvider, ulong blockNumber, ulong round = 0)
    {
        if (specProvider is XdcChainSpecBasedSpecProvider xdcProvider)
            return xdcProvider.GetXdcSpec(blockNumber, round);
        // Fallback for testing; note that this mutates the spec instance
        if (specProvider.GetSpec(blockNumber, null) is not IXdcReleaseSpec spec)
            throw new InvalidOperationException($"Expected {nameof(IXdcReleaseSpec)}.");
        spec.ApplyV2Config(round);
        return spec;
    }

    public static Address[] ParseV1Masternodes(this byte[] extraData)
    {
        int length = (extraData.Length - XdcConstants.ExtraVanity - XdcConstants.ExtraSeal) / Address.Size;
        if (length <= 0)
            throw new ArgumentException($"ExtraData too short to contain masternodes: length={extraData.Length}", nameof(extraData));
        Address[] masternodes = new Address[length];
        for (int i = 0; i < length; i++)
            masternodes[i] = new Address(extraData.AsSpan(XdcConstants.ExtraVanity + i * Address.Size, Address.Size));
        return masternodes;
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
        && (blockInfo.Round == blockHeader.ExtraConsensusData?.BlockRound);

    public static Signature DecodeSignature(this ref RlpReader decoderContext)
    {
        //includes the list prefix, which is 2 bytes for a 65 byte signature
        ReadOnlySpan<byte> sigBytes = decoderContext.PeekNextItem();
        if (sigBytes.Length != Signature.Size + 2)
            throw new RlpException($"Invalid signature length in '{nameof(Vote)}'");
        Signature signature = new(sigBytes.Slice(2, 64), sigBytes[66]);
        decoderContext.SkipItem();
        return signature;
    }

    public static bool IsGapPlusOne(this XdcSubnetBlockHeader header, IXdcReleaseSpec spec)
    {
        if (header.Number == 1)
            return true;
        // Guard against underflow: Gap should always be < EpochLength by configuration,
        // but we check explicitly rather than relying on that invariant holding at runtime.
        if (spec.Gap == 0 || spec.EpochLength <= spec.Gap)
            return false;
        return (header.Number % spec.EpochLength) == (spec.EpochLength - spec.Gap + 1);
    }

    /// <summary>
    /// Compares two lists of addresses for equality, ignoring order since the order of masternodes in XDC header validation does not matter.
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns>Returns <see cref="true"/> if the lists contain the same addresses, ignoring order; otherwise, <see cref="false"/>.</returns>
    public static bool ListsAreEqual(this IList<Address>? a, IList<Address>? b) => a is not null && b is not null && a.Count == b.Count && new HashSet<Address>(a).SetEquals(b);
}
