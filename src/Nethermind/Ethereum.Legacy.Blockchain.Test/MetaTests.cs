// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;

namespace Ethereum.Legacy.Blockchain.Test;

public class MetaTests : DirectoryMetaTests<StPrefix>
{
    protected override IEnumerable<string> FilterDirectories(IEnumerable<string> dirs) =>
        dirs.Where(d => d != "stEWASMTests");
}
