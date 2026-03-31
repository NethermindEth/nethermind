// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.Test.Base;

/// <summary>
/// Base class for MetaTests that verify every test directory has a corresponding test class.
/// Scans directories matching <typeparamref name="TPrefix"/> and checks the assembly for matching types.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class DirectoryMetaTests<TPrefix> where TPrefix : ITestDirectoryPrefix
{
    protected virtual string GetTestsDirectory() => AppDomain.CurrentDomain.BaseDirectory;

    protected virtual IEnumerable<string> FilterDirectories(IEnumerable<string> directories) => directories;

    [Test]
    public void All_categories_are_tested() =>
        TestDirectoryHelper.AssertAllCategoriesTested(GetType(),
            FilterDirectories(
                Directory.GetDirectories(GetTestsDirectory())
                    .Select(Path.GetFileName)
                    .Where(d => d.StartsWith(TPrefix.Value))),
            d => TestDirectoryHelper.GetClassNameFromDirectory(d, TPrefix.Value.Length));
}
