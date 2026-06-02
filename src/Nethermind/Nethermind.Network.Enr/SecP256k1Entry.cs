// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry storing the <see cref="CompressedPublicKey"/> of the node signer.
/// </summary>
public class SecP256k1Entry(CompressedPublicKey publicKey) : EnrContentEntry<CompressedPublicKey>(publicKey)
{
    public override string Key => EnrContentKey.SecP256k1;

    protected override int GetRlpLengthOfValue() => CompressedPublicKey.LengthInBytes + 1;

    protected override void EncodeValue(RlpStream rlpStream) => rlpStream.Encode(Value.Bytes);
}
