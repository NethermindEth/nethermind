// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry storing the <see cref="CompressedPublicKey"/> of the node signer.
/// </summary>
public class Secp256K1Entry : EnrContentEntry<CompressedPublicKey>
{
    public Secp256K1Entry(CompressedPublicKey publicKey) : base(publicKey) { }

    public override string Key => EnrContentKey.Secp256K1;

    protected override int GetRlpLengthOfValue()
    {
        return CompressedPublicKey.LengthInBytes + 1;
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        rlpStream.Encode(Value.Bytes);
    }
}
