/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nevermind.Network.Rlpx;
using NUnit.Framework;

namespace Nevermind.Network.Test.Rlpx
{
    [TestFixture]
    public class FrameCipherTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            byte[] message = {1, 2, 3, 4, 5, 6};
            byte[] encrypted = new byte[6];
            byte[] decrypted = new byte[6];

            FrameCipher frameCipher = new FrameCipher(NetTestVectors.AesSecret);
            frameCipher.Encrypt(message, 0, 6, encrypted, 0);
            frameCipher.Decrypt(encrypted, 0, 6, decrypted, 0);
            Assert.AreEqual(message, decrypted);
        }
        
        [Test]
        public void Can_run_twice()
        {
            byte[] message = {1, 2, 3, 4, 5, 6};
            byte[] encrypted = new byte[6];
            byte[] decrypted = new byte[6];

            FrameCipher frameCipher = new FrameCipher(NetTestVectors.AesSecret);
            frameCipher.Encrypt(message, 0, 6, encrypted, 0);
            frameCipher.Decrypt(encrypted, 0, 6, decrypted, 0);
            Assert.AreEqual(message, decrypted);
            
            Array.Clear(encrypted, 0, encrypted.Length);
            Array.Clear(decrypted, 0, decrypted.Length);
            frameCipher.Encrypt(message, 0, 6, encrypted, 0);
            frameCipher.Decrypt(encrypted, 0, 6, decrypted, 0);
            Assert.AreEqual(message, decrypted);
        }
        
        [Test]
        public void Can_do_inline()
        {
            byte[] message = {1, 2, 3, 4, 5, 6};
            byte[] messageClone = (byte[])message.Clone();

            FrameCipher frameCipher = new FrameCipher(NetTestVectors.AesSecret);
            frameCipher.Encrypt(messageClone, 0, 6, messageClone, 0);
            frameCipher.Decrypt(messageClone, 0, 6, messageClone, 0);
            Assert.AreEqual(message, messageClone);
        }
    }
}