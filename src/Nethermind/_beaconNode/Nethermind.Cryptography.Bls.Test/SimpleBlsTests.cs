//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core.Extensions;
using Nethermind.Cryptography;
using NUnit.Framework;

namespace Nethermind.Cryptography.Bls.Test
{
    [TestClass]
    public class SimpleBlsTests
    {
        [TestMethod]
        public void TestPrivateKeyToPublic()
        {
            var privateKeyBytes = Bytes.FromHexString("47b8192d77bf871b62e87859d653922725724a5c031afeabc60bcef5ff665138");

            Console.WriteLine("Serialized private key: {0}", privateKeyBytes.ToHexString());

            //BlsProxy.GetPublicKey(privateKeyBytes, out var publicKeySpan);
            var parameters = new BLSParameters()
            {
                PrivateKey = privateKeyBytes
            };
            using var bls = new BLSHerumi(parameters);
            var result = new byte[48];
            var success = bls.TryExportBlsPublicKey(result.AsSpan(), out var bytesWritten);
            
            //var publicKeyBytes = publicKeySpan.ToArray();
            var publicKeyBytes = result;

            Assert.AreEqual("b301803f8b5ac4a1133581fc676dfedc60d891dd5fa99028805e5ea5b08d3491af75d0707adab3b70c6a6a580217bf81", publicKeyBytes.ToHexString());
        }
    }
}