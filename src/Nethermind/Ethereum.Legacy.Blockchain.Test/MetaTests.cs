// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Legacy.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MetaTests
{
    [Test]
    public void All_categories_are_tested() =>
        TestDirectoryHelper.AssertAllCategoriesTested(GetType(),
            Directory.GetDirectories(AppDomain.CurrentDomain.BaseDirectory)
                .Select(Path.GetFileName)
                .Where(d => d.StartsWith("st") && d != "stEWASMTests"),
            d => TestDirectoryHelper.GetClassNameFromDirectory(d, 2));
}
