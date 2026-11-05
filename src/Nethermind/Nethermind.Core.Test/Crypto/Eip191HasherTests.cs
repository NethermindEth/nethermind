// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto
{
    [TestFixture]
    public class Eip191HasherTests
    {
        // Canonical EIP-191 / eth_sign test vector taken from the Ethereum Execution API spec
        // (docs-api/api/methods/eth_sign.mdx): signing message 0xdeadbeaf with the private key
        // for address 0x9b2055d370f73ec7d8a03e965129118dc8f5bf83 yields this signature.
        // Recovery exercises Eip191Hasher.HashMessage independently of any wallet implementation.
        [Test]
        public void HashMessage_KnownGethVector_RecoversExpectedAddress()
        {
            byte[] message = [0xde, 0xad, 0xbe, 0xaf];
            Signature signature = new("0xa3f20717a250c2b0b729b7e5becbff67fdaef7e0699da4de7ca5895b02a170a12d887fd3b17bfdce3481f10bea41f45ba9f709d39ce8325427b57afcfc994cee1b");
            Address expected = new("0x9b2055d370f73ec7d8a03e965129118dc8f5bf83");

            Address? recovered = new EthereumEcdsa(BlockchainIds.Mainnet).RecoverAddress(signature, Eip191Hasher.HashMessage(message));

            Assert.That(recovered, Is.EqualTo(expected));
        }
    }
}
