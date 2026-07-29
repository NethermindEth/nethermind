// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

public class BlockchainTestConversionTests
{
    [TestCase("Amsterdam")]
    [TestCase("ParisToShanghaiAtTime15k")]
    public void ConvertToBlockchainTests_PopulatesForkName(string network)
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
}
