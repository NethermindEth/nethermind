// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Libp2p.Core;
using NUnit.Framework;
using KeyType = Nethermind.Libp2p.Core.Dto.KeyType;

namespace Nethermind.BeaconChain.Test.P2P.Discovery;

/// <summary>
/// The libp2p peer id and the ENR are derived from the same stored key by two libraries; if they disagree after a restart,
/// every peer that dials this node from its ENR expects another peer id and the handshake fails.
/// </summary>
public class IdentityKeyTests
{
    [TestCase("0xb71c71a67e1177ad4e901695e1b4b9ee17ae16c6668d313eac2f96dbcda3f291", TestName = "EIP-778 key, top bit set")]
    [TestCase("0x0bd55a579d67617cdd7fcc99117c65c2eda9ee2cafe0634edefc7f6de9ed7b00", TestName = "Key ending in a zero byte")]
    [TestCase("0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318", TestName = "Key with the top bit clear")]
    public void A_stored_key_loads_as_the_identity_its_enr_advertises(string key)
    {
        byte[] stored = Bytes.FromHexString(key);

        Assert.That(BeaconP2P.IdentityFromStoredKey(stored).PeerId.ToString(), Is.EqualTo(BeaconDiscovery.DerivePeerId(new PrivateKey(stored).CompressedPublicKey)));
    }

    [Test]
    public void Generated_keys_load_as_the_identity_their_enr_advertises()
    {
        for (int i = 0; i < 256; i++)
        {
            Identity generated = new(privateKey: null, KeyType.Secp256K1);
            byte[] stored = generated.PrivateKey!.Data.ToByteArray();
            string loaded = BeaconP2P.IdentityFromStoredKey(stored).PeerId.ToString();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(loaded, Is.EqualTo(BeaconDiscovery.DerivePeerId(new PrivateKey(stored).CompressedPublicKey)), stored.ToHexString());
                // A first start runs on the generated identity, every restart on the stored bytes.
                Assert.That(generated.PeerId.ToString(), Is.EqualTo(loaded), stored.ToHexString());
            }
        }
    }
}
