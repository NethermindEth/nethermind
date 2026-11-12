// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class ContractAddressTests
{
    private static readonly Address Deployer = new("0x6ac7ea33f8831ea9dcc53393aaa88b25a785dbf0");

    [TestCase(0UL, "0xcd234a471b72ba2f1ccf0a70fcaba648a5eecd8d")]
    [TestCase(1UL, "0x343c43a37d37dff08ae8c4a11544c718abb4fcf8")]
    [TestCase(2UL, "0xf778b86fa74e846c4f0a1fbd1335fe81c00a0c91")]
    [TestCase(3UL, "0xfffd933a0bc612844eaf0c6fe3e5b8e9b6c1d19c")]
    public void Create_address_matches_known_vectors(ulong nonce, string expected) =>
        Assert.That(ContractAddress.From(Deployer, nonce), Is.EqualTo(new Address(expected)));

    /// <remarks>Nonces either side of each change in their encoded length, against the RLP encoding of the whole list.</remarks>
    [Test]
    public void Create_address_matches_the_rlp_of_deployer_and_nonce(
        [Values(0x7fUL, 0x80UL, 0xffUL, 0x100UL, 0xffffUL, 0x1_0000UL, 0xffff_ffffUL, 0x1_0000_0000UL, 0x00ff_ffff_ffff_ffffUL, 0x0100_0000_0000_0000UL, ulong.MaxValue)] ulong nonce)
    {
        byte[] list = Rlp.Encode(Rlp.Encode(Deployer.Bytes), Rlp.Encode((UInt256)nonce)).Bytes;
        Address expected = new(ValueKeccak.Compute(list));
        Assert.That(ContractAddress.From(Deployer, nonce), Is.EqualTo(expected));
    }
}
