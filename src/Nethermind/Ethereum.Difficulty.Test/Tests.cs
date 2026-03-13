// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test;

public class DifficultyByzantiumTests()
    : DifficultyHexTestFixture<DifficultyByzantiumTests>(new TestSingleReleaseSpecProvider(Byzantium.Instance));

public class DifficultyConstantinopleTests()
    : DifficultyHexTestFixture<DifficultyConstantinopleTests>(new TestSingleReleaseSpecProvider(Constantinople.Instance));

public class DifficultyCustomHomesteadTests()
    : DifficultyHexTestFixture<DifficultyCustomHomesteadTests>(new TestSingleReleaseSpecProvider(Homestead.Instance));

public class DifficultyCustomMainNetworkTests()
    : DifficultyHexTestFixture<DifficultyCustomMainNetworkTests>(MainnetSpecProvider.Instance);

public class DifficultyEIP2384Tests()
    : DifficultyHexTestFixture<DifficultyEIP2384Tests>(new TestSingleReleaseSpecProvider(MuirGlacier.Instance));

public class DifficultyEIP2384_randomTests()
    : DifficultyHexTestFixture<DifficultyEIP2384_randomTests>(new TestSingleReleaseSpecProvider(MuirGlacier.Instance));

public class DifficultyEIP2384_random_to20MTests()
    : DifficultyHexTestFixture<DifficultyEIP2384_random_to20MTests>(new TestSingleReleaseSpecProvider(MuirGlacier.Instance));

public class DifficultyFrontierTests()
    : DifficultyHexTestFixture<DifficultyFrontierTests>(new TestSingleReleaseSpecProvider(Frontier.Instance));

public class DifficultyHomesteadTests()
    : DifficultyHexTestFixture<DifficultyHomesteadTests>(new TestSingleReleaseSpecProvider(Homestead.Instance));

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

// Morden and Olympic tests are disabled — no test resource files exist
// ToDo: fix loader
