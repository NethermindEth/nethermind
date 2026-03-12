// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test;

[TestFixture]
public class MetaTests
{
    [Test]
    public void All_categories_are_tested()
    {
        string[] jsonFiles = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(f => f.StartsWith("difficulty"))
            .ToArray();
        Type[] types = GetType().Assembly.GetTypes();
        List<string> missingCategories = [];
        foreach (string file in jsonFiles)
        {
            string expected = ExpectedTypeName(file);
            if (types.All(t => !string.Equals(t.Name, expected, StringComparison.InvariantCultureIgnoreCase)))
                missingCategories.Add($"{file} expected {expected}");
        }

        foreach (string missing in missingCategories)
            Console.WriteLine($"{missing} category is missing");

        Assert.That(missingCategories.Where(x => !x.StartsWith("difficultyRopsten")), Is.Empty);
    }

    private static string ExpectedTypeName(string fileName)
    {
        string name = fileName;
        if (!name.EndsWith("Tests"))
            name = name.EndsWith("Test") ? name + "s" : name + "Tests";
        return name.Replace("_", string.Empty);
    }
}
