// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class MetaTests
    {
        [Test]
        public void All_categories_are_tested()
        {
            string[] directories =
                Directory.GetDirectories(AppDomain.CurrentDomain.BaseDirectory)
                .Select(Path.GetFileName)
                .Where(d => d.StartsWith("bc"))
                .ToArray();

            Type[] types = GetType().Assembly.GetTypes();
            List<string> missingCategories = new List<string>();
            foreach (string directory in directories)
            {
                string expectedTypeName = ExpectedTypeName(directory);
                if (types.All(t => !string.Equals(t.Name, expectedTypeName, StringComparison.InvariantCultureIgnoreCase)))
                {
                    if (new DirectoryInfo(directory).GetFiles().Any(f => f.Name.Contains(".resources.")))
                    {
                        continue;
                    }

                    missingCategories.Add(directory + " expected " + expectedTypeName);
                }
            }

            foreach (string missing in missingCategories)
            {
                Console.WriteLine($"{missing} category is missing");
            }

            Assert.AreEqual(0, missingCategories.Count);
        }

        private static string ExpectedTypeName(string directory)
        {
            string prefix = directory.StartsWith("vm") ? "Vm" : "";
            string expectedTypeName = directory.Remove(0, 2);
            if (!expectedTypeName.EndsWith("Tests"))
            {
                if (!expectedTypeName.EndsWith("Test"))
                {
                    expectedTypeName += "Tests";
                }
                else
                {
                    expectedTypeName += "s";
                }
            }

            return prefix + expectedTypeName;
        }
    }
}
