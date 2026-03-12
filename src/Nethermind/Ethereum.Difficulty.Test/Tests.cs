// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test;

[Parallelizable(ParallelScope.All)]
public class DifficultyByzantiumTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyByzantium.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new TestSingleReleaseSpecProvider(Byzantium.Instance));
}

[Parallelizable(ParallelScope.All)]
public class DifficultyConstantinopleTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyConstantinople.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new TestSingleReleaseSpecProvider(Constantinople.Instance));
}

[Parallelizable(ParallelScope.All)]
public class DifficultyCustomHomesteadTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyCustomHomestead.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new TestSingleReleaseSpecProvider(Homestead.Instance));
}

[Parallelizable(ParallelScope.All)]
public class DifficultyCustomMainNetworkTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyCustomMainNetwork.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, MainnetSpecProvider.Instance);
}

[Parallelizable(ParallelScope.All)]
public class DifficultyEIP2384Tests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyEIP2384.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new TestSingleReleaseSpecProvider(MuirGlacier.Instance));
}

[Parallelizable(ParallelScope.All)]
public class DifficultyEIP2384RandomTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyEIP2384_random.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new TestSingleReleaseSpecProvider(MuirGlacier.Instance));
}

[Parallelizable(ParallelScope.All)]
public class DifficultyEIP2384RandomTo20MTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyEIP2384_random_to20M.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new TestSingleReleaseSpecProvider(MuirGlacier.Instance));
}

[Parallelizable(ParallelScope.All)]
public class DifficultyFrontierTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyFrontier.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new TestSingleReleaseSpecProvider(Frontier.Instance));
}

[Parallelizable(ParallelScope.All)]
public class DifficultyHomesteadTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyHomestead.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new TestSingleReleaseSpecProvider(Homestead.Instance));
}

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

[Parallelizable(ParallelScope.All)]
public class DifficultyMordenTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyMorden.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new MordenSpecProvider());
}

[Parallelizable(ParallelScope.All)]
public class DifficultyOlympicTests : TestsBase
{
    public static IEnumerable<DifficultyTests> LoadTests() => LoadHex("difficultyOlympic.json");

    [TestCaseSource(nameof(LoadTests))]
    public void Test(DifficultyTests test) => RunTest(test, new TestSingleReleaseSpecProvider(Olympic.Instance));
}
