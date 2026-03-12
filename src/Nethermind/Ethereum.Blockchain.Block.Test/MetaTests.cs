// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Block.Test;

public class MetaTests : DirectoryMetaTests<BcPrefix>
{
    protected override IEnumerable<string> FilterDirectories(IEnumerable<string> dirs)
    {
        string baseDir = GetTestsDirectory();
        return dirs.Where(d => !new DirectoryInfo(Path.Combine(baseDir, d))
            .GetFiles().Any(f => f.Name.Contains(".resources.")));
    }
}
