// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Legacy.VM.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MetaTests
{
    [Test]
    public void All_categories_are_tested()
    {
        string[] directories = Directory.GetDirectories(AppDomain.CurrentDomain.BaseDirectory)
            .Select(Path.GetFileName)
            .Where(d => d.StartsWith("vm"))
            .ToArray();

        Type[] types = GetType().Assembly.GetTypes();
        List<string> missingCategories = [];
        foreach (string directory in directories)
        {
            string expected = TestDirectoryHelper.GetClassNameFromDirectory(directory, 2);
            if (types.All(t => !string.Equals(t.Name, expected, StringComparison.InvariantCultureIgnoreCase)))
                missingCategories.Add($"{directory} expected {expected}");
        }

        foreach (string missing in missingCategories)
            Console.WriteLine($"{missing} category is missing");

        Assert.That(missingCategories, Is.Empty);
    }
}
