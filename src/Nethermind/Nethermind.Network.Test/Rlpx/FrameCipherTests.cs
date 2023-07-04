// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.Rlpx;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class FrameCipherTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            byte[] message = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            byte[] encrypted = new byte[16];
            byte[] decrypted = new byte[16];

            FrameCipher frameCipher = new(NetTestVectors.AesSecret);
            frameCipher.Encrypt(message, 0, 16, encrypted, 0);
            frameCipher.Decrypt(encrypted, 0, 16, decrypted, 0);
            Assert.That(decrypted, Is.EqualTo(message));
        }

        [Test]
        public void Can_run_twice()
        {
            byte[] message = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            byte[] encrypted = new byte[16];
            byte[] decrypted = new byte[16];

            FrameCipher frameCipher = new(NetTestVectors.AesSecret);
            frameCipher.Encrypt(message, 0, 16, encrypted, 0);
            frameCipher.Decrypt(encrypted, 0, 16, decrypted, 0);
            Assert.That(decrypted, Is.EqualTo(message));

            Array.Clear(encrypted, 0, encrypted.Length);
            Array.Clear(decrypted, 0, decrypted.Length);
            frameCipher.Encrypt(message, 0, 16, encrypted, 0);
            frameCipher.Decrypt(encrypted, 0, 16, decrypted, 0);
            Assert.That(decrypted, Is.EqualTo(message));
        }

        [Test]
        public void Should_not_return_same_value_when_used_twice_with_same_input()
        {
            byte[] message = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            byte[] encrypted1 = new byte[16];
            byte[] encrypted2 = new byte[16];

            FrameCipher frameCipher = new(NetTestVectors.AesSecret);
            frameCipher.Encrypt(message, 0, 16, encrypted1, 0);
            frameCipher.Encrypt(message, 0, 16, encrypted2, 0);
            Assert.That(encrypted2, Is.Not.EqualTo(encrypted1));
        }

        [Test]
        public void Can_run_twice_longer_message()
        {
            int length = 16;

            byte[] message = new byte[length * 2];
            message[3] = 123;
            message[4] = 123;
            message[5] = 12;

            byte[] encrypted = new byte[length];
            byte[] decrypted = new byte[2 * length];

            FrameCipher frameCipher = new(NetTestVectors.AesSecret);
            frameCipher.Encrypt(message, 0, length, encrypted, 0);
            frameCipher.Decrypt(encrypted, 0, length, decrypted, 0);
            Assert.That(decrypted, Is.EqualTo(message));

            Array.Clear(encrypted, 0, encrypted.Length);
            Array.Clear(decrypted, 0, decrypted.Length);
            frameCipher.Encrypt(message, 0, length, encrypted, 0);
            frameCipher.Decrypt(encrypted, 0, length, decrypted, 0);
            Assert.That(decrypted, Is.EqualTo(message));
        }

        [Test]
        public void Can_do_inline()
        {
            byte[] message = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            byte[] messageClone = (byte[])message.Clone();

            FrameCipher frameCipher = new(NetTestVectors.AesSecret);
            frameCipher.Encrypt(messageClone, 0, 16, messageClone, 0);
            frameCipher.Decrypt(messageClone, 0, 16, messageClone, 0);
            Assert.That(messageClone, Is.EqualTo(message));
        }
    }
}
