// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Xdc;
using Nethermind.Xdc.Spec;
using Nethermind.Core.Specs;

namespace Nethermind.Crypto;
public static class XdcExtentions
{
    //TODO can we wire up this so we can use Rlp.Encode()?
    private static XdcHeaderDecoder _headerDecoder = new();
    public static Signature Sign(this IEthereumEcdsa ecdsa, PrivateKey privateKey, XdcBlockHeader header)
    {
        ValueHash256 hash = ValueKeccak.Compute(_headerDecoder.Encode(header, RlpBehaviors.ForSealing).Bytes);
        return ecdsa.Sign(privateKey, in hash);
    }

    public static Hash256 CalculateHash(this XdcBlockHeader header)
        => new Hash256(header.CalculateValueHash(RlpBehaviors.None));

    public static ValueHash256 CalculateValueHash(this XdcBlockHeader header, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        KeccakRlpStream stream = new();
        _headerDecoder.Encode(stream, header, behaviors);
        return stream.GetValueHash();
    }

    public static XdcReleaseSpec GetXdcSpec(this ISpecProvider specProvider, XdcBlockHeader xdcBlockHeader)
    {
        XdcReleaseSpec spec = specProvider.GetSpec(xdcBlockHeader) as XdcReleaseSpec;
        if (spec is null)
            throw new InvalidOperationException($"Expected {nameof(XdcReleaseSpec)}.");
        return spec;
    }
}
