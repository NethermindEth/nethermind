// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Legacy.Transition.Test;

public class MetaTests : DirectoryMetaTests<BcPrefix>
{
    protected override string GetTestsDirectory() => Path.Combine(base.GetTestsDirectory(), "Tests");

    protected override IEnumerable<string> FilterDirectories(IEnumerable<string> dirs) =>
        dirs.Except(["bcArrowGlacierToMerge", "bcArrowGlacierToParis"]);
}

[NonParallelizable]
public class BerlinToLondon : BlockchainTestFixture<BerlinToLondon>;

public class ByzantiumToConstantinopleFix : BlockchainTestFixture<ByzantiumToConstantinopleFix>;

public class EIP158ToByzantium : BlockchainTestFixture<EIP158ToByzantium>;

public class FrontierToHomestead : BlockchainTestFixture<FrontierToHomestead>;

public class HomesteadToDao : BlockchainTestFixture<HomesteadToDao>;

public class HomesteadToEIP150 : BlockchainTestFixture<HomesteadToEIP150>;

public class MergeToShanghai : LegacyBlockchainTestFixture<MergeToShanghai>;
