// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        string[] directories =
            Directory.GetDirectories(baseDir)
            .Select(Path.GetFileName)
            .Where(d => d.StartsWith("bc"))
            .ToArray();

        Type[] types = GetType().Assembly.GetTypes();
        List<string> missingCategories = [];
        foreach (string directory in directories)
        {
            string expectedTypeName = ExpectedTypeName(directory);
            if (types.All(t => !string.Equals(t.Name, expectedTypeName, StringComparison.InvariantCultureIgnoreCase)))
            {
                if (new DirectoryInfo(Path.Combine(baseDir, directory)).GetFiles().Any(f => f.Name.Contains(".resources.")))
                    continue;

                missingCategories.Add(directory + " expected " + expectedTypeName);
            }
        }

        foreach (string missing in missingCategories)
        {
            Console.WriteLine($"{missing} category is missing");
        }

        Assert.That(missingCategories.Count, Is.EqualTo(0));
    }

    private static string ExpectedTypeName(string directory)
    {
        string name = directory[2..];
        name = name.Replace('-', '_');
        if (name.Length > 0 && char.IsDigit(name[0]))
            name = "_" + name;
        return name;
    }
}
