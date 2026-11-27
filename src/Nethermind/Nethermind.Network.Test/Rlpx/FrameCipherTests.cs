// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
            frameCipher.Encrypt(message.AsSpan(0, 16), encrypted);
            frameCipher.Decrypt(encrypted.AsSpan(0, 16), decrypted);
            Assert.That(decrypted, Is.EqualTo(message));
        }

        [Test]
        public void Can_run_twice()
        {
            byte[] message = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            byte[] encrypted = new byte[16];
            byte[] decrypted = new byte[16];

            FrameCipher frameCipher = new(NetTestVectors.AesSecret);
            frameCipher.Encrypt(message.AsSpan(0, 16), encrypted);
            frameCipher.Decrypt(encrypted.AsSpan(0, 16), decrypted);
            Assert.That(decrypted, Is.EqualTo(message));

            Array.Clear(encrypted, 0, encrypted.Length);
            Array.Clear(decrypted, 0, decrypted.Length);
            frameCipher.Encrypt(message.AsSpan(0, 16), encrypted);
            frameCipher.Decrypt(encrypted.AsSpan(0, 16), decrypted);
            Assert.That(decrypted, Is.EqualTo(message));
        }

        [Test]
        public void Should_not_return_same_value_when_used_twice_with_same_input()
        {
            byte[] message = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            byte[] encrypted1 = new byte[16];
            byte[] encrypted2 = new byte[16];

            FrameCipher frameCipher = new(NetTestVectors.AesSecret);
            frameCipher.Encrypt(message.AsSpan(0, 16), encrypted1);
            frameCipher.Encrypt(message.AsSpan(0, 16), encrypted2);
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
            frameCipher.Encrypt(message.AsSpan(0, length), encrypted.AsSpan(0, length));
            frameCipher.Decrypt(encrypted.AsSpan(0, length), decrypted.AsSpan(0, length));
            Assert.That(decrypted, Is.EqualTo(message));

            Array.Clear(encrypted, 0, encrypted.Length);
            Array.Clear(decrypted, 0, decrypted.Length);
            frameCipher.Encrypt(message.AsSpan(0, length), encrypted.AsSpan(0, length));
            frameCipher.Decrypt(encrypted.AsSpan(0, length), decrypted.AsSpan(0, length));
            Assert.That(decrypted, Is.EqualTo(message));
        }

        [Test]
        public void Can_do_inline()
        {
            byte[] message = Enumerable.Range(1, 48).Select(static i => (byte)i).ToArray();
            byte[] messageClone = (byte[])message.Clone();
            byte[] encrypted = new byte[message.Length];
            new FrameCipher(NetTestVectors.AesSecret).Encrypt(message, encrypted);

            FrameCipher frameCipher = new(NetTestVectors.AesSecret);
            frameCipher.Encrypt(messageClone, messageClone);
            Assert.That(messageClone, Is.EqualTo(encrypted), "in place, as into a separate buffer");
            frameCipher.Decrypt(messageClone, messageClone);
            Assert.That(messageClone, Is.EqualTo(message));
        }
    }
}
