// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Ethereum.Basic.Test
{
    [Parallelizable(ParallelScope.All)]
    public class TransactionTests
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        public static IEnumerable<TransactionTest> LoadTests()
        {
            return TestLoader.LoadFromFile<TransactionTestJson[], TransactionTest>(
                "txtest.json",
                jsonArray => jsonArray.Select(Convert));
        }

        [TestCaseSource(nameof(LoadTests))]
        public void Test(TransactionTest test)
        {
            EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance);
            Transaction decodedUnsigned = Rlp.Decode<Transaction>(test.Unsigned);
            Assert.That(decodedUnsigned.Value, Is.EqualTo(test.Value), "value");
            Assert.That(decodedUnsigned.GasPrice, Is.EqualTo(test.GasPrice), "gasPrice");
            Assert.That(decodedUnsigned.GasLimit, Is.EqualTo(test.StartGas), "gasLimit");
            Assert.That(decodedUnsigned.Data, Is.EqualTo(test.Data), "data");
            Assert.That(decodedUnsigned.To, Is.EqualTo(test.To), "to");
            Assert.That(decodedUnsigned.Nonce, Is.EqualTo(test.Nonce), "nonce");

            Transaction decodedSigned = Rlp.Decode<Transaction>(test.Signed);
            ethereumEcdsa.Sign(test.PrivateKey, decodedUnsigned, false);
            Assert.That(decodedUnsigned.Signature.R, Is.EqualTo(decodedSigned.Signature.R), "R");
            BigInteger expectedS = decodedSigned.Signature.S.ToUnsignedBigInteger();
            BigInteger actualS = decodedUnsigned.Signature.S.ToUnsignedBigInteger();
            BigInteger otherS = EthereumEcdsa.LowSTransform - actualS;

            // test does not use normalized signature
            if (otherS != expectedS && actualS != expectedS)
            {
                throw new Exception("S is wrong");
            }

            ulong vToCompare = decodedUnsigned.Signature.V;
            if (otherS == decodedSigned.Signature.S.ToUnsignedBigInteger())
            {
                vToCompare = vToCompare == 27ul ? 28ul : 27ul;
            }

            Assert.That(vToCompare, Is.EqualTo(decodedSigned.Signature.V), "V");
        }

        private static TransactionTest Convert(TransactionTestJson testJson)
        {
            TransactionTest test = new TransactionTest();
            test.Value = (UInt256)testJson.Value;
            test.Data = Bytes.FromHexString(testJson.Data);
            test.GasPrice = (UInt256)testJson.GasPrice;
            test.PrivateKey = new PrivateKey(testJson.Key);
            test.Nonce = (UInt256)testJson.Nonce;
            test.Signed = new Rlp(Bytes.FromHexString(testJson.Signed));
            byte[] unsigned = Bytes.FromHexString(testJson.Unsigned);
            if (unsigned[0] == 0xf8)
            {
                unsigned[1] -= 3;
            }
            else
            {
                unsigned[0] -= 3;
            }

            test.Unsigned = new Rlp(unsigned.Slice(0, unsigned.Length - 3));
            test.StartGas = testJson.StartGas;
            test.To = string.IsNullOrEmpty(testJson.To) ? null : new Address(testJson.To);
            return test;
        }

        private class TransactionTestJson
        {
            public string Key { get; set; }
            public long Nonce { get; set; }
            public long GasPrice { get; set; }
            public long StartGas { get; set; }
            public string To { get; set; }
            public long Value { get; set; }
            public string Data { get; set; }
            public string Unsigned { get; set; }
            public string Signed { get; set; }
        }

        [DebuggerDisplay("{PrivateKey}")]
        public class TransactionTest
        {
            public PrivateKey PrivateKey { get; set; }
            public UInt256 Nonce { get; set; }
            public UInt256 GasPrice { get; set; }
            public long StartGas { get; set; }
            public Address To { get; set; }
            public UInt256 Value { get; set; }
            public byte[] Data { get; set; }
            public Rlp Unsigned { get; set; }
            public Rlp Signed { get; set; }

            public override string ToString()
            {
                return PrivateKey.ToString();
            }
        }
    }
}
