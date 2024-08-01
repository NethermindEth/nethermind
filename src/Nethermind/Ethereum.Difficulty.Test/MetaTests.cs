// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [TestFixture]
    public class MetaTests
    {
        [Test]
        public void All_categories_are_tested()
        {
            string[] directories = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(f => f.StartsWith("difficulty"))
                .ToArray();
            Type[] types = GetType().Assembly.GetTypes();
            List<string> missingCategories = new List<string>();
            foreach (string directory in directories)
            {
                string expectedTypeName = ExpectedTypeName(directory);
                if (types.All(t => !string.Equals(t.Name, expectedTypeName, StringComparison.InvariantCultureIgnoreCase)))
                {
                    missingCategories.Add(directory);
                }
            }

            foreach (string missing in missingCategories)
            {
                Console.WriteLine($"{missing} category is missing");
            }

            Assert.That(missingCategories.Where(x => x != "difficultyRopsten").Count, Is.EqualTo(0));
        }

        private static string ExpectedTypeName(string directory)
        {
            string expectedTypeName = directory;
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

            return expectedTypeName.Replace("_", string.Empty);
        }
    }
}
