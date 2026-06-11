// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>The <c>eth2</c> ENR entry: the SSZ-encoded <see cref="EnrForkId"/> as an RLP byte string.</summary>
internal sealed class Eth2Entry(byte[] sszValue) : EnrContentEntry<byte[]>(sszValue)
{
    public override string Key => EnrContentKey.Eth2;

    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);

    protected override void EncodeValue(RlpStream rlpStream) => rlpStream.Encode(Value);
}
