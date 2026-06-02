// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using NUnit.Framework;

namespace Nethermind.Core.Test.Memory;

public class MallocHelperTests
{
    [Test]
    public void TestMallOpts() => Assert.That(MallocHelper.Instance.MallOpt(MallocHelper.Option.M_MMAP_THRESHOLD, (int)128.KiB), Is.True);

    [Test]
    public void TestMallocTrim() => Assert.That(MallocHelper.Instance.MallocTrim(0), Is.True);
}
