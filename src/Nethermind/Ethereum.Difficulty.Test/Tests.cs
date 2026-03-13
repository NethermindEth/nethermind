// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
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

public class DifficultyByzantiumTests()
    : DifficultyHexTestFixture<DifficultyByzantiumTests>(
        new TestSingleReleaseSpecProvider(new OverridableReleaseSpec(Byzantium.Instance) { DifficultyBombDelay = 0 }));

public class DifficultyConstantinopleTests()
    : DifficultyHexTestFixture<DifficultyConstantinopleTests>(
        new TestSingleReleaseSpecProvider(new OverridableReleaseSpec(Constantinople.Instance) { DifficultyBombDelay = 0 }));

public class DifficultyEIP2384Tests()
    : DifficultyHexTestFixture<DifficultyEIP2384Tests>(
        new TestSingleReleaseSpecProvider(new OverridableReleaseSpec(MuirGlacier.Instance) { DifficultyBombDelay = 0 }));

public class DifficultyEIP2384_randomTests()
    : DifficultyHexTestFixture<DifficultyEIP2384_randomTests>(
        new TestSingleReleaseSpecProvider(new OverridableReleaseSpec(MuirGlacier.Instance) { DifficultyBombDelay = 0 }));

public class DifficultyEIP2384_random_to20MTests()
    : DifficultyHexTestFixture<DifficultyEIP2384_random_to20MTests>(
        new TestSingleReleaseSpecProvider(new OverridableReleaseSpec(MuirGlacier.Instance) { DifficultyBombDelay = 0 }));

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
