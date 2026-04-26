// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Transaction.Test;

public class TransactionTests
{
    [OneTimeSetUp]
    public void SetUp() => Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

    private static readonly string[] TestSets =
        ["Address", "Data", "EIP2028", "GasLimit", "GasPrice", "Nonce", "RSValue", "Signature", "Value", "WrongRLP"];

    private static IEnumerable<TransactionTest> LoadAllTests() => TestSets.SelectMany(LoadTests);

    private static IEnumerable<TransactionTest> LoadTests(string testSet)
    {
        List<TransactionTest> tests = [];
        foreach (string testDir in Directory.EnumerateDirectories(".", "tt" + testSet))
        {
            foreach (string testFile in Directory.EnumerateFiles(testDir))
            {
                string json = File.ReadAllText(testFile);
                Dictionary<string, TransactionTestJson> testsInFile = JsonSerializer.Deserialize<Dictionary<string, TransactionTestJson>>(json);
                foreach (KeyValuePair<string, TransactionTestJson> namedTest in testsInFile)
                {
                    tests.Add(CreateTest(testDir, namedTest.Key, namedTest.Value));
                }
            }
        }

        return tests;
    }

    private static TransactionTest CreateTest(string network, string name, TransactionTestJson testJson)
    {
        TransactionJson transactionJson = testJson.Transaction;
        if (transactionJson is null)
            return new TransactionTest(network, name, testJson.Rlp);

        return new ValidTransactionTest(network, name, testJson.Rlp)
        {
            BlockNumber = Bytes.FromHexString(testJson.BlockNumber).ToUInt256(),
            Data = Bytes.FromHexString(transactionJson.Data),
            GasLimit = Bytes.FromHexString(transactionJson.GasLimit).ToUInt256(),
            GasPrice = Bytes.FromHexString(transactionJson.GasPrice).ToUInt256(),
            Nonce = Bytes.FromHexString(transactionJson.Nonce).ToUInt256(),
            R = Bytes.FromHexString(transactionJson.R).ToUInt256(),
            S = Bytes.FromHexString(transactionJson.S).ToUInt256(),
            V = Bytes.FromHexString(transactionJson.V)[0],
            Sender = new Address(testJson.Sender),
            Value = Bytes.FromHexString(transactionJson.Value).ToUInt256(),
            To = string.IsNullOrEmpty(transactionJson.To) ? null : new Address(transactionJson.To)
        };
    }

    [TestCaseSource(nameof(LoadAllTests))]
    public void Test(TransactionTest test) => RunTest(test, Frontier.Instance);

    private static void RunTest(TransactionTest test, IReleaseSpec spec)
    {
        ValidTransactionTest validTest = test as ValidTransactionTest;
        Nethermind.Core.Transaction transaction;
        try
        {
            Rlp rlp = new(Bytes.FromHexString(test.Rlp));
            transaction = Rlp.Decode<Nethermind.Core.Transaction>(rlp);
        }
        catch (Exception)
        {
            if (validTest is null)
                return;

            throw;
        }

        bool useChainId = transaction.Signature.V > 28UL;
        TxValidator validator = new(useChainId ? BlockchainIds.Mainnet : 0UL);

        if (validTest is not null)
        {
            Assert.That(transaction.Value, Is.EqualTo(validTest.Value), "value");
            Assert.That(transaction.Data.AsArray(), Is.EqualTo(validTest.Data), "data");
            Assert.That(transaction.GasLimit, Is.EqualTo(validTest.GasLimit.ToInt64(null)), "gasLimit");
            Assert.That(transaction.GasPrice, Is.EqualTo(validTest.GasPrice), "gasPrice");
            Assert.That(transaction.Nonce, Is.EqualTo(validTest.Nonce), "nonce");
            Assert.That(transaction.To, Is.EqualTo(validTest.To), "to");
            Assert.That(validator.IsWellFormed(transaction, spec), Is.True);

            Signature expectedSignature = new(validTest.R, validTest.S, validTest.V);
            Assert.That(transaction.Signature, Is.EqualTo(expectedSignature), "signature");

            IEthereumEcdsa ecdsa = new EthereumEcdsa(useChainId ? BlockchainIds.Mainnet : 0UL);
            bool verified = ecdsa.Verify(validTest.Sender, transaction);
            Assert.That(verified, Is.True);
        }
        else
        {
            Assert.That(validator.IsWellFormed(transaction, spec), Is.False);
        }
    }

    public class TransactionTest(string network, string name, string rlp)
    {
        public string Network { get; set; } = network;
        public string Name { get; set; } = name;
        public string Rlp { get; set; } = rlp;

        public override string ToString() => string.Concat(Network, "->", Name);
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

    public class ValidTransactionTest(string network, string name, string rlp)
        : TransactionTest(network, name, rlp)
    {
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
