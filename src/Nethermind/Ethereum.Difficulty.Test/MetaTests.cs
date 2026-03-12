// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test;

[TestFixture]
public class MetaTests
{
    [Test]
    public void All_categories_are_tested() =>
        TestDirectoryHelper.AssertAllCategoriesTested(GetType(),
            Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(f => f.StartsWith("difficulty") && !f.StartsWith("difficultyRopsten")),
            ExpectedTypeName);

    private static string ExpectedTypeName(string fileName)
    {
        string name = fileName;
        if (!name.EndsWith("Tests"))
            name = name.EndsWith("Test") ? name + "s" : name + "Tests";
        return name.Replace("_", string.Empty);
    }
}
