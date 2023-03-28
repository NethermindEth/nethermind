// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
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
                new();
            foreach (string testDir in testDirs)
            {
                testJsons[testDir] = new Dictionary<string, TransactionTestJson>();
                IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir).ToList();
                foreach (string testFile in testFiles)
                {
                    string json = File.ReadAllText(testFile);
                    Dictionary<string, TransactionTestJson> testsInFile = JsonConvert.DeserializeObject<Dictionary<string, TransactionTestJson>>(json);
                    foreach (KeyValuePair<string, TransactionTestJson> namedTest in testsInFile)
                    {
                        testJsons[testDir].Add(namedTest.Key, namedTest.Value);
                    }
                }
            }

            List<TransactionTest> tests = new();
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
                        validTest.BlockNumber = Bytes.FromHexString(byName.Value.BlockNumber).ToUInt256();
                        validTest.Data = Bytes.FromHexString(transactionJson.Data);
                        validTest.GasLimit = Bytes.FromHexString(transactionJson.GasLimit).ToUInt256();
                        validTest.GasPrice = Bytes.FromHexString(transactionJson.GasPrice).ToUInt256();
                        validTest.Nonce = Bytes.FromHexString(transactionJson.Nonce).ToUInt256();
                        validTest.R = Bytes.FromHexString(transactionJson.R).ToUInt256();
                        validTest.S = Bytes.FromHexString(transactionJson.S).ToUInt256();
                        validTest.V = Bytes.FromHexString(transactionJson.V)[0];
                        validTest.Sender = new Address(byName.Value.Sender);
                        validTest.Value = Bytes.FromHexString(transactionJson.Value).ToUInt256();
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

        [TestCaseSource(nameof(LoadTests), new object[] { "Address" })]
        public void Test_Address(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Data" })]
        public void Test_Data(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "EIP2028" })]
        public void Test_EIP2028(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "GasLimit" })]
        public void Test_GasLimit(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "GasPrice" })]
        public void Test_GasPrice(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Nonce" })]
        public void Test_Nonce(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "RSValue" })]
        public void Test_RSValue(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Signature" })]
        public void Test_Signature(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Value" })]
        public void Test_Value(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        // ToDo: this tests are not starting because of TargetInvocationException
        // [TestCaseSource(nameof(LoadTests), new object[] { "VValue" })]
        // public void Test_VValue(TransactionTest test)
        // {
        //     RunTest(test, Frontier.Instance);
        // }

        [TestCaseSource(nameof(LoadTests), new object[] { "WrongRLP" })]
        public void Test_WrongRLP(TransactionTest test)
        {
            RunTest(test, Frontier.Instance);
        }

        private void RunTest(TransactionTest test, IReleaseSpec spec)
        {
            //TestContext.CurrentContext.Test.Properties.Set("Category", test.Network); // no longer public

            ValidTransactionTest validTest = test as ValidTransactionTest;
            Nethermind.Core.Transaction transaction;
            try
            {
                Rlp rlp = new(Bytes.FromHexString(test.Rlp));
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

            bool useChainId = transaction.Signature.V > 28UL;
            ulong chainId = useChainId ? BlockchainIds.Mainnet : 0UL;
            ISpecProvider specProvider = new SingleReleaseSpecProvider(spec, chainId, chainId);
            TxValidator validator = new(specProvider);

            if (validTest != null)
            {
                Assert.AreEqual(validTest.Value, transaction.Value, "value");
                Assert.AreEqual(validTest.Data, transaction.Data, "data");
                Assert.AreEqual(validTest.GasLimit, transaction.GasLimit, "gasLimit");
                Assert.AreEqual(validTest.GasPrice, transaction.GasPrice, "gasPrice");
                Assert.AreEqual(validTest.Nonce, transaction.Nonce, "nonce");
                Assert.AreEqual(validTest.To, transaction.To, "to");
                Assert.True(validator.IsWellFormed(transaction, spec));

                Signature expectedSignature = new(validTest.R, validTest.S, validTest.V);
                Assert.AreEqual(expectedSignature, transaction.Signature, "signature");

                IEthereumEcdsa ecdsa = new EthereumEcdsa(useChainId ? BlockchainIds.Mainnet : 0UL, LimboLogs.Instance);
                bool verified = ecdsa.Verify(
                    validTest.Sender,
                    transaction);
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
            public UInt256 BlockNumber { get; set; }
            public UInt256 Nonce { get; set; }
            public UInt256 GasPrice { get; set; }
            public UInt256 GasLimit { get; set; }
            public Address To { get; set; }
            public UInt256 Value { get; set; }
            public byte V { get; set; }
            public UInt256 R { get; set; }
            public UInt256 S { get; set; }
            public byte[] Data { get; set; }
        }
    }
}
