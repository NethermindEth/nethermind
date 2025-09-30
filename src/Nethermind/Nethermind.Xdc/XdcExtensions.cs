// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using System;
using Nethermind.Xdc;
using Nethermind.Xdc.Spec;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Types;
using Nethermind.Core;

namespace Nethermind.Crypto;
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
}
