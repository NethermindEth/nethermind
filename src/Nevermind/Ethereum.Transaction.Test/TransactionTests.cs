using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Nevermind.Blockchain.Validators;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;
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
                        validTest.BlockNumber = byName.Value.BlockNumber;
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
            RunTest(test, true);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Eip155VitaliksEip158" })]
        public void Test_eip155VitaliksEip158(TransactionTest test)
        {
            RunTest(test, true);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Eip155VitaliksHomesead" })]
        public void Test_eip155VitaliksHomesead(TransactionTest test)
        {
            RunTest(test, true);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Eip158" })]
        public void Test_eip158(TransactionTest test)
        {
            RunTest(test);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Frontier" })]
        public void Test_frontier(TransactionTest test)
        {
            RunTest(test);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "Homestead" })]
        public void Test_homestead(TransactionTest test)
        {
            RunTest(test);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "SpecConstantinople" })]
        public void Test_spec_constantinople(TransactionTest test)
        {
            RunTest(test, true, true);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "VRuleEip158" })]
        public void Test_v_rule_eip158(TransactionTest test)
        {
            RunTest(test, true);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "WrongRLPFrontier" })]
        public void Test_wrong_rlp_frontier(TransactionTest test)
        {
            RunTest(test);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "WrongRLPHomestead" })]
        public void Test_wrong_rlp_homestead(TransactionTest test)
        {
            RunTest(test);
        }

        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroSigConstantinople" })]
        public void Test_zero_sig_constantinople(TransactionTest test)
        {
            RunTest(test, true, true);
        }

        private void RunTest(TransactionTest test, bool eip155 = false, bool ignoreSignatures = false)
        {
            TestContext.CurrentContext.Test.Properties.Set("Category", test.Network);

            if (test is ValidTransactionTest validTest)
            {
                Rlp rlp = new Rlp(Hex.ToBytes(validTest.Rlp));

                Nevermind.Core.Transaction transaction = new Nevermind.Core.Transaction();
                transaction.Value = validTest.Value;
                transaction.GasLimit = validTest.GasLimit;
                transaction.GasPrice = validTest.GasPrice;

                if (validTest.To != null)
                {
                    transaction.Data = validTest.Data;
                    transaction.To = validTest.To;
                }
                else
                {
                    transaction.Init = validTest.Data;
                }

                transaction.Nonce = validTest.Nonce;

                // signatures have zeroes trimmed in testing so not obtaining the same values
                //Rlp testRlp = Rlp.EncodeBigInteger(transaction, false);
                //Assert.AreEqual(rlp, testRlp);

                Nevermind.Core.Transaction decodedTransaction = Rlp.Decode<Nevermind.Core.Transaction>(rlp);
                Assert.AreEqual(transaction.Value, decodedTransaction.Value, "value");
                Assert.True(Bytes.UnsafeCompare(transaction.Data, decodedTransaction.Data), "date");
                Assert.AreEqual(transaction.GasLimit, decodedTransaction.GasLimit, "gasLimit");
                Assert.AreEqual(transaction.GasPrice, decodedTransaction.GasPrice, "gasPrice");
                Assert.AreEqual(transaction.Init, decodedTransaction.Init, "init");
                Assert.AreEqual(transaction.Nonce, decodedTransaction.Nonce, "nonce");
                Assert.AreEqual(transaction.To, decodedTransaction.To, "to");
                //Assert.True(TransactionValidator.IsValid(transaction));

                if (!ignoreSignatures)
                {
                    Signature signature = new Signature(validTest.R, validTest.S, validTest.V);
                    transaction.Signature = signature;

                    int chainIdValue =
                        validTest.V > 28
                            ? validTest.V % 2 == 1
                                ? (validTest.V - 35) / 2
                                : (validTest.V - 36) / 2
                            : 1;

                    bool useEip155Rule = eip155 && validTest.V > 28;

                    Assert.AreEqual(transaction.Signature, decodedTransaction.Signature, "signature");

                    bool verfiied = Signer.Verify(
                        validTest.Sender,
                        transaction,
                        useEip155Rule,
                        (ChainId)chainIdValue);
                    Assert.True(verfiied);
                }
            }
            else
            {
                Assert.Throws(Is.InstanceOf<Exception>(), () =>
                {
                    Rlp rlp = new Rlp(Hex.ToBytes(test.Rlp));
                    Nevermind.Core.Transaction transaction = Rlp.Decode<Nevermind.Core.Transaction>(rlp);
                    Assert.True(TransactionValidator.IsWellFormed(transaction));
                });
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
            public ulong BlockNumber { get; set; }
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