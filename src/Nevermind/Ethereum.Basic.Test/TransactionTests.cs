using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using JetBrains.Annotations;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using NUnit.Framework;

namespace Ethereum.Basic.Test
{
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
            Transaction transaction = new Transaction();
            transaction.Value = test.Value;
            
            transaction.GasLimit = test.StartGas;
            transaction.GasPrice = test.GasPrice;
            if (test.To == null)
            {
                transaction.Init = test.Data;
            }
            else
            {
                transaction.To = test.To;
                transaction.Data = test.Data;
            }
            
            transaction.Nonce = test.Nonce;

            TestContext.WriteLine("Testing unsigned...");
            Rlp unsignedRlp = Rlp.Encode(transaction);
            Assert.AreEqual(test.Unsigned, unsignedRlp, "unsigned");

            TestContext.WriteLine("Unsigned is fine, testing signed...");
            Signer.Sign(transaction, test.PrivateKey);
           
            Address address = Signer.Recover(transaction);
            Assert.AreEqual(test.PrivateKey.Address, address);

            // decode test rlp into transaction and recover address...
            // confirm it is correct

            // can signature differ?
            // Rlp signedRlp = Rlp.EncodeBigInteger(transaction);
            // Assert.AreEqual(test.Signed, signedRlp, "signed");
        }

        private static TransactionTest Convert(TransactionTestJson testJson)
        {
            TransactionTest test = new TransactionTest();
            test.Value = testJson.Value;
            test.Data = Hex.ToBytes(testJson.Data);
            test.GasPrice = testJson.GasPrice;
            test.PrivateKey = new PrivateKey(testJson.Key);
            test.Nonce = testJson.Nonce;
            test.Signed = new Rlp(Hex.ToBytes(testJson.Signed));
            test.Unsigned = new Rlp(Hex.ToBytes(testJson.Unsigned));
            test.StartGas = testJson.StartGas;
            test.To = string.IsNullOrEmpty(testJson.To) ? null : new Address(testJson.To);
            return test;
        }

        [UsedImplicitly]
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
            public BigInteger Nonce { get; set; }
            public BigInteger GasPrice { get; set; }
            public BigInteger StartGas { get; set; }
            public Address To { get; set; }
            public BigInteger Value { get; set; }
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