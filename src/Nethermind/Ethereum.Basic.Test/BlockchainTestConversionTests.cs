// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

public class BlockchainTestConversionTests
{
    [Test]
    public void ConvertToBlockchainTests_PopulatesForkName([Values("Amsterdam", "ParisToShanghaiAtTime15k")] string network)
    {
        string json = $$"""
            {
              "tests/some/test.py::test_case[fork_{{network}}-blockchain_test]": {
                "network": "{{network}}",
                "lastblockhash": "0x281f01f5b9b9a5237ec39ac315e2e3a01017c37a83f6f0e26689b29e421a0311",
                "pre": {}
              }
            }
            """;

        List<BlockchainTest> tests = [.. JsonToEthereumTest.ConvertToBlockchainTests(json)];

        Assert.That(tests, Has.Count.EqualTo(1));
        Assert.That(tests.Single().ForkName, Is.EqualTo(network));
    }

    [TestCase("AmsterdamBPOIncrease", 21, 28, 0xd6f3c4)]
    [TestCase("AmsterdamToBPOIncreaseAtTime15k", 21, 28, 0xd6f3c4)]
    [TestCase("AmsterdamBPODecrease", 9, 14, 0x729e16)]
    [TestCase("AmsterdamToBPODecreaseAtTime15k", 9, 14, 0x729e16)]
    public void ConvertToBlockchainTests_LoadsBpoScenarioAsItsForkWithScenarioBlobSchedule(
        string network, int targetBlobCount, int maxBlobCount, int baseFeeUpdateFraction)
    {
        string json = $$"""
            {
              "tests/some/test.py::test_case[fork_{{network}}-blockchain_test]": {
                "network": "{{network}}",
                "lastblockhash": "0x281f01f5b9b9a5237ec39ac315e2e3a01017c37a83f6f0e26689b29e421a0311",
                "pre": {},
                "config": {
                  "network": "{{network}}",
                  "chainid": "0x01",
                  "blobSchedule": {
                    "Amsterdam": { "target": "0x0e", "max": "0x15", "baseFeeUpdateFraction": "0xb24b3f" },
                    "AmsterdamBPOIncrease": { "target": "0x15", "max": "0x1c", "baseFeeUpdateFraction": "0xd6f3c4" },
                    "AmsterdamBPODecrease": { "target": "0x09", "max": "0x0e", "baseFeeUpdateFraction": "0x729e16" }
                  }
                }
              }
            }
            """;

        BlockchainTest test = JsonToEthereumTest.ConvertToBlockchainTests(json).Single();
        IReleaseSpec scenario = test.NetworkAfterTransition ?? test.Network!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.IsEip7928Enabled, Is.True);
            Assert.That(scenario.TargetBlobCount, Is.EqualTo(targetBlobCount));
            Assert.That(scenario.MaxBlobCount, Is.EqualTo(maxBlobCount));
            Assert.That(scenario.BlobBaseFeeUpdateFraction, Is.EqualTo(baseFeeUpdateFraction));
        }
    }

    [TestCase("blockAccessList", TestName = "Access list of a valid block")]
    [TestCase("rlp_decoded", TestName = "Access list of an expected-invalid block")]
    public void DeliveredBlockAccessList_is_read_from_where_the_fixture_puts_it(string location)
    {
        const string accessList = """
            [{
              "address": "0x000f3df6d732807ef1319fb7b8bb8522d0beac02",
              "nonceChanges": [{ "blockAccessIndex": "0x01", "postNonce": "0x01" }],
              "balanceChanges": [{ "blockAccessIndex": "0x01", "postBalance": "0x0a" }],
              "codeChanges": [],
              "storageChanges": [{ "slot": "0x01", "slotChanges": [{ "blockAccessIndex": "0x01", "postValue": "0x02" }] }],
              "storageReads": ["0x03"]
            }]
            """;
        string block = location == "rlp_decoded"
            ? $$"""{ "rlp": "0x", "expectException": "BlockException.INVALID_BLOCK_ACCESS_LIST", "rlp_decoded": { "blockAccessList": {{accessList}} } }"""
            : $$"""{ "rlp": "0x", "blockAccessList": {{accessList}} }""";

        TestBlockJson testBlock = new EthereumJsonSerializer().Deserialize<TestBlockJson>(block);
        ReadOnlyBlockAccessList delivered = Rlp.Decode<ReadOnlyBlockAccessList>(BlockAccessListAccountJson.Encode(testBlock.DeliveredBlockAccessList!));

        ReadOnlyAccountChanges account = delivered.AccountChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Address, Is.EqualTo(new Address("0x000f3df6d732807ef1319fb7b8bb8522d0beac02")));
            Assert.That(account.NonceChanges, Is.EqualTo(new[] { new NonceChange(1, 1) }));
            Assert.That(account.BalanceChanges, Is.EqualTo(new[] { new BalanceChange(1, 10) }));
            Assert.That(account.StorageChanges.Single().Changes, Is.EqualTo(new[] { new StorageChange(1, 2) }));
            Assert.That(account.StorageReads, Is.EqualTo(new[] { (UInt256)3 }));
        }
    }

    [Test]
    public void TryDeliver_attaches_a_list_whose_hash_is_the_headers_commitment()
    {
        byte[] encoded = BlockAccessListAccountJson.Encode([Account()]);
        Block block = Build.A.Block.WithBlockAccessListHash(Keccak.Compute(encoded)).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BlockAccessListAccountJson.TryDeliver(block, [Account()]), Is.True);
            Assert.That(block.EncodedBlockAccessList, Is.EqualTo(encoded));
            Assert.That(block.BlockAccessList!.AccountChanges.Single().Address, Is.EqualTo(new Address(AccountAddress)));
        }
    }

    [Test]
    public void TryDeliver_drops_a_list_that_does_not_match_the_header()
    {
        Block block = Build.A.Block.WithBlockAccessListHash(Keccak.OfAnEmptySequenceRlp).TestObject;

        AssertDropped(block, [Account()]);
    }

    [Test]
    public void TryDeliver_rejects_a_malformed_list_the_header_commits_to()
    {
        // A duplicated account, which the client's decoder rejects.
        BlockAccessListAccountJson[] malformed = [Account(), Account()];
        Block block = Build.A.Block.WithBlockAccessListHash(Keccak.Compute(BlockAccessListAccountJson.Encode(malformed))).TestObject;

        Assert.That(() => BlockAccessListAccountJson.TryDeliver(block, malformed),
            Throws.InstanceOf<RlpException>().With.Message.Contains("incorrect order"));
    }

    [Test]
    public void A_block_whose_list_was_not_delivered_is_reported_with_why([Values("bad-access-list", "withheld")] string why)
    {
        Block undelivered = Build.A.Block.WithNumber(1).TestObject;
        Block withoutList = Build.A.Block.WithNumber(2).TestObject;
        List<string> reasons = [];
        IBlockAccessListExecutionReport report = new BlockchainTestBase.UndeliveredAccessListObserver(
            new ReasonRecorder(reasons), new Dictionary<Hash256, string> { [undelivered.Hash!] = why });

        report.OnExecutionPathChosen(undelivered, "no-access-list");
        report.OnExecutionPathChosen(undelivered, "disabled");
        report.OnExecutionPathChosen(undelivered, null);
        report.OnExecutionPathChosen(withoutList, "no-access-list");

        Assert.That(reasons, Is.EqualTo(new[] { why, why, null, "no-access-list" }));
    }

    private const string AccountAddress = "0x000f3df6d732807ef1319fb7b8bb8522d0beac02";

    private static BlockAccessListAccountJson Account() => new() { Address = AccountAddress };

    private static void AssertDropped(Block block, BlockAccessListAccountJson[] delivered)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BlockAccessListAccountJson.TryDeliver(block, delivered), Is.False);
            Assert.That(block.BlockAccessList, Is.Null);
            Assert.That(block.EncodedBlockAccessList, Is.Null);
        }
    }

    private sealed class ReasonRecorder(List<string> reasons) : IBlockAccessListExecutionReport
    {
        public void OnExecutionPathChosen(Block block, string sequentialReason) => reasons.Add(sequentialReason);

        public void OnSequentialRetry(Block block, Exception parallelError, Exception sequentialError)
        {
        }
    }
}
