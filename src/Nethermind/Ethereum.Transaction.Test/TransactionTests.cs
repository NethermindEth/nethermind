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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.Transaction.Test
{
    public class TransactionTests
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static IEnumerable<TransactionTest> LoadTests(string testSet)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            IEnumerable<string> testDirs = Directory.EnumerateDirectories(".", "tt" + testSet);
            Dictionary<string, Dictionary<string, TransactionTestJson>> testJsons =
                new Dictionary<string, Dictionary<string, TransactionTestJson>>();
            foreach (string testDir in testDirs)
            {
                testJsons[testDir] = new Dictionary<string, TransactionTestJson>();
                IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
                foreach (string testFile in testFiles)
                {
                    // am I sure?
                    if (testFile.Contains("_gnv") || testFile.Contains("TransactionWithRvaluePrefixed00"))
                    {
                        continue;
                    }

                    string json = File.ReadAllText(testFile);
                    Dictionary<string, TransactionTestJson> testsInFile = JsonConvert.DeserializeObject<Dictionary<string, TransactionTestJson>>(json);
                    foreach (KeyValuePair<string, TransactionTestJson> namedTest in testsInFile)
                    {
                        testJsons[testDir].Add(namedTest.Key, namedTest.Value);
                    }
                }
            }

            List<TransactionTest> tests = new List<TransactionTest>();
            foreach (KeyValuePair<string, Dictionary<string, TransactionTestJson>> byDir in testJsons)
            {
                foreach (KeyValuePair<string, TransactionTestJson> byName in byDir.Value)
                {
                    TransactionJson transactionJson = byName.Value.Transaction;
                    TransactionTest test;
                    if (transactionJson != null)
                    {
                        test = new ValidTransactionTest(byDir.Key, byName.Key, byName.Value.Rlp);
                        ValidTransactionTest validTest = (ValidTransactionTest)test;
                        validTest.BlockNumber = Hex.ToBytes(byName.Value.BlockNumber).ToUnsignedBigInteger();
                        validTest.Data = Hex.ToBytes(transactionJson.Data);
                        validTest.GasLimit = Hex.ToBytes(transactionJson.GasLimit).ToUnsignedBigInteger();
                        validTest.GasPrice = Hex.ToBytes(transactionJson.GasPrice).ToUnsignedBigInteger();
                        validTest.Nonce = Hex.ToBytes(transactionJson.Nonce).ToUnsignedBigInteger();
                        validTest.R = Hex.ToBytes(transactionJson.R).ToUnsignedBigInteger();
                        validTest.S = Hex.ToBytes(transactionJson.S).ToUnsignedBigInteger();
                        validTest.V = Hex.ToBytes(transactionJson.V)[0];
                        validTest.Sender = new Address(byName.Value.Sender);
                        validTest.Value = Hex.ToBytes(transactionJson.Value).ToUnsignedBigInteger();
                        validTest.To = string.IsNullOrEmpty(transactionJson.To) ? null : new Address(transactionJson.To);
                    }
                    else
                    {
                        test = new TransactionTest(byDir.Key, byName.Key, byName.Value.Rlp);
                    }

                    tests.Add(test);
                }
            }

            return tests;
        }
        
        [TestCaseSource(nameof(LoadTests), new object[] { "Constantinople" })]
        public void Test_constantinople(TransactionTest test)
        {
            RunTest(test, Byzantium.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Eip155VitaliksEip158" })]
        public void Test_eip155VitaliksEip158(TransactionTest test)
        {
            RunTest(test, SpuriousDragon.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Eip155VitaliksHomesead" })]
        public void Test_eip155VitaliksHomesead(TransactionTest test)
        {
            RunTest(test, Homestead.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Eip158" })]
        public void Test_eip158(TransactionTest test)
        {
            RunTest(test, SpuriousDragon.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Frontier" })]
        public void Test_frontier(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Homestead" })]
        public void Test_homestead(TransactionTest test)
        {
            RunTest(test, Homestead.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "SpecConstantinople" })]
        public void Test_spec_constantinople(TransactionTest test)
        {
            RunTest(test, Byzantium.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "VRuleEip158" })]
        public void Test_v_rule_eip158(TransactionTest test)
        {
            RunTest(test, SpuriousDragon.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "WrongRLPFrontier" })]
        public void Test_wrong_rlp_frontier(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "WrongRLPHomestead" })]
        public void Test_wrong_rlp_homestead(TransactionTest test)
        {
            RunTest(test, Homestead.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroSigConstantinople" })]
        public void Test_zero_sig_constantinople(TransactionTest test)
        {
            RunTest(test, Byzantium.Instance);
        }

        private void RunTest(TransactionTest test, IReleaseSpec spec)
        {
            TestContext.CurrentContext.Test.Properties.Set("Category", test.Network);

            ValidTransactionTest validTest = test as ValidTransactionTest;
            Nethermind.Core.Transaction transaction;
            try
            {
                Rlp rlp = new Rlp(Hex.ToBytes(test.Rlp));
                transaction = Rlp.Decode<Nethermind.Core.Transaction>(rlp);
            }
            catch (Exception)
            {
                if (validTest == null)
                {
                    return;
                }

                throw;
            }

            bool useChainId = transaction.Signature.V > 28;            
            
            SignatureValidator signatureValidator = new SignatureValidator(useChainId ? ChainId.MainNet : 0);
            TransactionValidator validator = new TransactionValidator(signatureValidator);

            if (validTest != null)
            {
                Assert.AreEqual(validTest.Value, transaction.Value, "value");
                Assert.AreEqual(validTest.Data, transaction.Data ?? transaction.Init, "data");
                Assert.AreEqual(validTest.GasLimit, transaction.GasLimit, "gasLimit");
                Assert.AreEqual(validTest.GasPrice, transaction.GasPrice, "gasPrice");
                Assert.AreEqual(validTest.Nonce, transaction.Nonce, "nonce");
                Assert.AreEqual(validTest.To, transaction.To, "to");
                Assert.True(validator.IsWellFormed(transaction, spec));

                Signature expectedSignature = new Signature(validTest.R, validTest.S, validTest.V);
                Assert.AreEqual(expectedSignature, transaction.Signature, "signature");
//                if(useChainId && spec.IsEip155Enabled)
//                
                IEthereumSigner signer = new EthereumSigner(new SingleReleaseSpecProvider(spec, useChainId ? (int)ChainId.MainNet : 0), NullLogger.Instance);
                bool verified = signer.Verify(
                    validTest.Sender,
                    transaction,
                    0);
                Assert.True(verified);
            }
            else
            {
                Assert.False(validator.IsWellFormed(transaction, spec));
            }
        }

        public class TransactionTest
        {
            public TransactionTest(string network, string name, string rlp)
            {
                Network = network;
                Name = name;
                Rlp = rlp;
            }

            public string Network { get; set; }
            public string Name { get; set; }
            public string Rlp { get; set; }

            public override string ToString()
            {
                return string.Concat(Network, "->", Name);
            }
        }

        public class TransactionJson
        {
            public string Nonce { get; set; }
            public string GasPrice { get; set; }
            public string GasLimit { get; set; }
            public string To { get; set; }
            public string Value { get; set; }
            public string V { get; set; }
            public string R { get; set; }
            public string S { get; set; }
            public string Data { get; set; }
        }

        public class TransactionTestJson
        {
            public string Rlp { get; set; }
            public string Sender { get; set; }
            public string BlockNumber { get; set; }
            public TransactionJson Transaction { get; set; }
        }

        public class ValidTransactionTest : TransactionTest
        {
            public ValidTransactionTest(string network, string name, string rlp)
                : base(network, name, rlp)
            {
            }

            public Address Sender { get; set; }
            public BigInteger BlockNumber { get; set; }
            public BigInteger Nonce { get; set; }
            public BigInteger GasPrice { get; set; }
            public BigInteger GasLimit { get; set; }
            public Address To { get; set; }
            public BigInteger Value { get; set; }
            public byte V { get; set; }
            public BigInteger R { get; set; }
            public BigInteger S { get; set; }
            public byte[] Data { get; set; }
        }
    }
}