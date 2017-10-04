using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.Test.Base
{
    public static class TestLoader
    {
        public static IEnumerable<TTest> LoadFromFile<TContainer, TTest>(
            string testFileName,
            Func<TContainer, IEnumerable<TTest>> testExtractor)
        {
            Assembly assembly = typeof(TTest).Assembly;
            string[] resourceNames = assembly.GetManifestResourceNames();
            string resourceName = resourceNames.SingleOrDefault(r => r.Contains(testFileName));
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                Assert.NotNull(stream);
                using (StreamReader reader = new StreamReader(stream))
                {
                    string testJson = reader.ReadToEnd();
                    TContainer testSpecs =
                        JsonConvert.DeserializeObject<TContainer>(testJson);
                    return testExtractor(testSpecs);
                }
            }
        }
    }
}