using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class MetaTests
    {
        [Test]
        public void All_categories_are_tested()
        {
            string[] directories = Directory.GetDirectories(AppDomain.CurrentDomain.BaseDirectory)
                .Select(Path.GetFileName)
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

            Assert.AreEqual(0, missingCategories.Count);
        }

        private static string ExpectedTypeName(string directory)
        {
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

            return expectedTypeName;
        }
    }
}