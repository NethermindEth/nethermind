// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Test.Base;

namespace Ethereum.Legacy.Transition.Test;

public class MetaTests : DirectoryMetaTests<BcPrefix>
{
    protected override string GetTestsDirectory() => Path.Combine(base.GetTestsDirectory(), "Tests");

    protected override IEnumerable<string> FilterDirectories(IEnumerable<string> dirs) =>
        dirs.Except(["bcArrowGlacierToMerge", "bcArrowGlacierToParis"]);
}
