// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using Nethermind.Core.Specs;
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
}
