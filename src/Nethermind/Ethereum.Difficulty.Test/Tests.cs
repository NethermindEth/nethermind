// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test;

public class DifficultyCustomHomesteadTests()
    : DifficultyHexTestFixture<DifficultyCustomHomesteadTests>(new TestSingleReleaseSpecProvider(Homestead.Instance));

public class DifficultyCustomMainNetworkTests()
    : DifficultyHexTestFixture<DifficultyCustomMainNetworkTests>(MainnetSpecProvider.Instance);

public class DifficultyFrontierTests()
    : DifficultyHexTestFixture<DifficultyFrontierTests>(new TestSingleReleaseSpecProvider(Frontier.Instance));

public class DifficultyHomesteadTests()
    : DifficultyHexTestFixture<DifficultyHomesteadTests>(new TestSingleReleaseSpecProvider(Homestead.Instance));

// Byzantium, Constantinople, and EIP2384 tests are disabled — difficulty bomb calculations
// don't match the Ethereum Foundation test expectations (pre-existing, disabled on master too).

[Parallelizable(ParallelScope.All)]
public class DifficultyMainNetworkTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadBasicTests() => Load("difficulty.json");

    public static IEnumerable<DifficultyTests> LoadMainTests() => LoadHex("difficultyMainNetwork.json");

    [TestCaseSource(nameof(LoadBasicTests))]
    public void Test_basic(DifficultyTests test) => RunTest(test, MainnetSpecProvider.Instance);

    [TestCaseSource(nameof(LoadMainTests))]
    public void Test_main(DifficultyTests test) => RunTest(test, MainnetSpecProvider.Instance);
}
