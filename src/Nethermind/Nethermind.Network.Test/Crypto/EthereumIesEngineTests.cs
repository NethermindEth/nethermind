// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Network.Test.Builders;

namespace Nethermind.Network.Test.Crypto;

[Parallelizable(ParallelScope.Self)]
public class EthereumIesEngineTests
{
    // Mirrors ECIES settings used in EciesCipher
    private const int KeySize = 128; // bits

    [Test]
    public void Decrypt_throws_when_cipher_body_not_greater_than_mac_tag()
    {
        TestRandom cryptoRandom = new();
        EciesCipher ecies = new(cryptoRandom);

        byte[] fixedIv = Bytes.FromHexString("0x0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a");
        cryptoRandom.EnqueueRandomBytes(fixedIv);
        cryptoRandom.EnqueueRandomBytes(NetTestVectors.EphemeralKeyA.KeyBytes);

        PrivateKey recipient = NetTestVectors.StaticKeyA;
        byte[] plain = [1, 2, 3, 4, 5];
        byte[] validCipher = ecies.Encrypt(recipient.PublicKey, plain, []);

        int ephemLen = 65;
        int ivLen = KeySize / 8;
        int prefixLen = ephemLen + ivLen;

        byte[] broken = new byte[prefixLen + 32];
        Array.Copy(validCipher, 0, broken, 0, prefixLen);

        Exception ex = Assert.Throws<Exception>(() => ecies.Decrypt(recipient, broken));
        Assert.That(ex.GetType().Name, Is.EqualTo("InvalidCipherTextException"));
    }
}
