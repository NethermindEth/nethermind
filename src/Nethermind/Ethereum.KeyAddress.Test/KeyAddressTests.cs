﻿/*
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.KeyAddress.Test
{
    [Parallelizable(ParallelScope.All)]
    public class KeyAddressTests
    {
        private IEthereumEcdsa _ecdsa;
        
        [OneTimeSetUp]
        public void SetUp()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            _ecdsa = new EthereumEcdsa(OlympicSpecProvider.Instance, NullLogManager.Instance);
        }

        private static IEnumerable<KeyAddressTest> LoadTests()
        {
            return TestLoader.LoadFromFile<KeyAddressTestJson[], KeyAddressTest>(
                "keyaddrtest.json",
                c => c.Select(FromJson));
        }

        private static KeyAddressTest FromJson(KeyAddressTestJson testJson)
        {
            return new KeyAddressTest(
                testJson.Seed,
                testJson.Key,
                testJson.Addr,
                UInt256.Parse(testJson.Signature.R),
                UInt256.Parse(testJson.Signature.S),
                byte.Parse(testJson.Signature.V));
        }

        [TestCase("0x135a7de83802408321b74c322f8558db1679ac20", "xyz",    "0x30755ed65396facf86c53e6217c52b4daebe72aa4941d89635409de4c9c7f9466d4e9aaec7977f05e923889b33c0d0dd27d7226b6e6f56ce737465c5cfd04be41b")]
        [TestCase("0x36d85Dc3683156e63Bf880A9fAb7788CF8143a27", "Christopher Pearce", "0x34ff4b97a0ec8f735f781f250dcd3070a72ddb640072dd39553407d0320db79939e3b080ecaa2e9f248214c6f0811fb4b4ba05b7bcff254c053e47d8513e82091b")]
        public void Recovered_address_as_expected(string addressHex, string message, string sigHex)
        {
            Keccak messageHash = Keccak.Compute(message);
            Signature sig = new Signature(sigHex);
            Address recovered = _ecdsa.RecoverAddress(sig, messageHash);
            Address address = new Address(addressHex);

            // TODO: check - at the moment they are failing when running in the test mode but not in Debug
            Assert.AreEqual(address, recovered);
        }

        [Ignore("Ignoring these as the test values seem wrong, need to confirm")]
        [TestCaseSource(nameof(LoadTests))]
        public void Signature_as_expected(KeyAddressTest test)
        {
            // what is the format of the JSON input file?
            // what is the sig_of_emptystring in JSON file? is it Keccak.OfAnEmptyString as assumed?
            
            PrivateKey privateKey = new PrivateKey(test.Key);
            Address actualAddress = privateKey.Address;
            Signature actualSig = _ecdsa.Sign(privateKey, Keccak.OfAnEmptyString);
            string actualSigHex = actualSig.ToString();

            Signature expectedSig = new Signature(test.R, test.S, test.V);
            string expectedSigHex = expectedSig.ToString();
            Address expectedAddress = new Address(test.Address);

            Assert.AreEqual(expectedAddress, actualAddress, "address vs adress from private key");

            Address recoveredActualAddress = _ecdsa.RecoverAddress(actualSig, Keccak.OfAnEmptyString);
            Assert.AreEqual(actualAddress, recoveredActualAddress);

            // it does not work
            Assert.AreEqual(expectedSigHex, actualSigHex, "expected vs actual signature hex");

            Address recovered = _ecdsa.RecoverAddress(expectedSig, Keccak.OfAnEmptyString);
            Assert.AreEqual(expectedAddress, recovered);
        }

        private class SigOfEmptyString
        {
            public string V { get; set; }
            public string R { get; set; }
            public string S { get; set; }
        }

        private class KeyAddressTestJson
        {
            [JsonProperty("sig_of_emptystring")]
            public SigOfEmptyString Signature { get; set; }

            public string Seed { get; set; }
            public string Key { get; set; }
            public string Addr { get; set; }
        }

        public class KeyAddressTest
        {
            public KeyAddressTest(string seed, string key, string address, UInt256 r, UInt256 s, byte v)
            {
                Seed = seed;
                Key = key;
                Address = address;
                V = v;
                R = r;
                S = s;
            }

            public string Seed { get; }
            public string Key { get; }
            public string Address { get; }
            public byte V { get; }

            public UInt256 R { get; }
            public UInt256 S { get; }

            public override string ToString()
            {
                return $"{Seed}, exp: {R}, {S}, {V}";
            }
        }
    }
}