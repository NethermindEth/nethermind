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

    [TestCase("0x6ac7ea33f8831ea9dcc53393aaa88b25a785dbf0", 0UL, "0xcd234a471b72ba2f1ccf0a70fcaba648a5eecd8d")]
    [TestCase("0x6ac7ea33f8831ea9dcc53393aaa88b25a785dbf0", 1UL, "0x343c43a37d37dff08ae8c4a11544c718abb4fcf8")]
    [TestCase("0x6ac7ea33f8831ea9dcc53393aaa88b25a785dbf0", 2UL, "0xf778b86fa74e846c4f0a1fbd1335fe81c00a0c91")]
    [TestCase("0x6ac7ea33f8831ea9dcc53393aaa88b25a785dbf0", 3UL, "0xfffd933a0bc612844eaf0c6fe3e5b8e9b6c1d19c")]
    [TestCase("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099", 0UL, "0x24cd2edba056b7c654a50e8201b619d4f624fdda")]
    [TestCase("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099", 1UL, "0xdc98b4d0af603b4fb5ccdd840406a0210e5deff8")]
    [TestCase("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099", 0x7fUL, "0xb0099f030fbd2b512080790d100a3a3819ce577f")]
    [TestCase("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099", 0x80UL, "0x3b71de3573667d0860bff1e4d40d9e07264623aa")]
    [TestCase("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099", 0xffUL, "0x198411bfc0fbbeef7194152584c712412e2f3ea5")]
    [TestCase("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099", 0x100UL, "0x66136d11dbd7227e6f5ac5588797899704be8348")]
    [TestCase("0xb7705ae4c6f81b66cdb323c65f4e8133690fc099", ulong.MaxValue, "0x7d707106c1e88d29f7585d237f9848e1c33cacfd")]
    public void Create_address_matches_known_vectors(string deployer, ulong nonce, string expected) =>
        Assert.That(ContractAddress.From(new Address(deployer), nonce), Is.EqualTo(new Address(expected)));

    /// <remarks>Nonces either side of each change in their encoded length, against the RLP encoding of the whole list.</remarks>
    [Test]
    public void Create_address_matches_the_rlp_of_deployer_and_nonce(
        [Values(0x7fUL, 0x80UL, 0xffUL, 0x100UL, 0xffffUL, 0x1_0000UL, 0xffff_ffffUL, 0x1_0000_0000UL, 0x00ff_ffff_ffff_ffffUL, 0x0100_0000_0000_0000UL, ulong.MaxValue)] ulong nonce) =>
        Assert.That(ContractAddress.From(Deployer, nonce), Is.EqualTo(FromRlp(Deployer, nonce)));

    /// <remarks>A null deployer or a nonce over 64 bits takes the general RLP path.</remarks>
    [Test]
    public void Create_address_on_the_general_path_matches_the_rlp([Values] bool nullDeployer, [Values(0, 64)] int nonceShift)
    {
        Address? deployer = nullDeployer ? null : Deployer;
        UInt256 nonce = UInt256.One << nonceShift;
        Assert.That(ContractAddress.From(deployer, nonce), Is.EqualTo(FromRlp(deployer, nonce)));
    }

    private static Address FromRlp(Address? deployer, in UInt256 nonce)
    {
        byte[] list = Rlp.Encode(deployer is null ? Rlp.OfEmptyByteArray : Rlp.Encode(deployer.Bytes), Rlp.Encode(nonce)).Bytes;
        return new(ValueKeccak.Compute(list));
    }
}
