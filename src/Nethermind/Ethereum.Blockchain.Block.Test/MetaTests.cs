// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MetaTests
{
    [Test]
    public void All_categories_are_tested()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        TestDirectoryHelper.AssertAllCategoriesTested(GetType(),
            Directory.GetDirectories(baseDir)
                .Select(Path.GetFileName)
                .Where(d => d.StartsWith("bc"))
                .Where(d => !new DirectoryInfo(Path.Combine(baseDir, d)).GetFiles().Any(f => f.Name.Contains(".resources."))),
            d => TestDirectoryHelper.GetClassNameFromDirectory(d, 2));
    }
}
