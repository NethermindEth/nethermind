// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;
using NUnit.Framework;

namespace Ethereum.Legacy.Transition.Test;

public class MetaTests : DirectoryMetaTests<BcPrefix>
{
    protected override string GetTestsDirectory() => Path.Combine(base.GetTestsDirectory(), "Tests");
}

public class ArrowGlacierToParis : LegacyBlockchainTestFixture<ArrowGlacierToParis>;

[NonParallelizable]
public class BerlinToLondon : LegacyBlockchainTestFixture<BerlinToLondon>;

public class ByzantiumToConstantinopleFix : LegacyBlockchainTestFixture<ByzantiumToConstantinopleFix>;

public class EIP158ToByzantium : LegacyBlockchainTestFixture<EIP158ToByzantium>;

public class FrontierToHomestead : LegacyBlockchainTestFixture<FrontierToHomestead>;

public class HomesteadToDao : LegacyBlockchainTestFixture<HomesteadToDao>;

public class HomesteadToEIP150 : LegacyBlockchainTestFixture<HomesteadToEIP150>;

public class MergeToShanghai : LegacyBlockchainTestFixture<MergeToShanghai>;

[TestFixture]
public class ForkClassificationTests : BlockchainTestBase
{
    private static IEnumerable<TestCaseData> PostMergeClassificationCases()
    {
        yield return new TestCaseData(London.Instance, false).SetName("London is pre-merge");
        yield return new TestCaseData(ArrowGlacier.Instance, false).SetName("ArrowGlacier is pre-merge");
        yield return new TestCaseData(GrayGlacier.Instance, false).SetName("GrayGlacier is pre-merge");
        yield return new TestCaseData(Paris.Instance, true).SetName("Paris is post-merge");
        yield return new TestCaseData(LondonGnosis.Instance, false).SetName("LondonGnosis is pre-merge");
        yield return new TestCaseData(ShanghaiGnosis.Instance, true).SetName("ShanghaiGnosis is post-merge");
    }

    [TestCaseSource(nameof(PostMergeClassificationCases))]
    public void IsPostMergeSpec_returns_expected_value(IReleaseSpec spec, bool expectedIsPostMerge) =>
        Assert.That(IsPostMergeSpec(spec), Is.EqualTo(expectedIsPostMerge));
}
