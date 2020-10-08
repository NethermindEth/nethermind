using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    public class PasswordProviderTests
    {
        private static List<(string Name, string Content)> _files = new List<(string Name, string Content)>()
        {
            ("TestingPasswordProviderFileF1", "PF1"),
            ("TestingPasswordProviderFileF2", "PF2")
        };

        [SetUp]
        public void SetUp()
        {
            var resourcePath = PathUtils.GetApplicationResourcePath(string.Empty);
            foreach (var file in _files)
            {
                var filePath = Path.Combine(resourcePath, file.Name);
                if (!File.Exists(filePath))
                {
                    File.Create(filePath).Close();
                    File.WriteAllText(filePath, file.Content);
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            var resourcePath = PathUtils.GetApplicationResourcePath(string.Empty);
            foreach (var file in _files)
            {
                var filePath = Path.Combine(resourcePath, file.Name);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        public static IEnumerable<PasswordProviderTest> PasswordProviderTestCases
        {
            get
            {
                yield return new PasswordProviderTest()
                {
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[0].Name },
                    ExpectedPasswords = new[] { _files[0].Content, _files[0].Content },
                };

                yield return new PasswordProviderTest()
                {
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[0].Name, _files[1].Name },
                    ExpectedPasswords = new[] { _files[0].Content, _files[1].Content },
                };

                yield return new PasswordProviderTest()
                {
                    Passwords = new[] { "A", "B" },
                    ExpectedPasswords = new[] { "A", "B" },
                };

                yield return new PasswordProviderTest()
                {
                    Passwords = new[] { "A", "B" },
                    ExpectedPasswords = new[] { "A", "B" },
                };
            }
        }

        [Test]
        public void GetPassword([ValueSource(nameof(PasswordProviderTestCases))] PasswordProviderTest test)
        {
            IKeyStoreConfig keyStoreConfig = Substitute.For<IKeyStoreConfig>();
            keyStoreConfig.Passwords.Returns(test.Passwords);
            keyStoreConfig.PasswordFiles.Returns(_files.Where(x => test.PasswordFiles.Contains(x.Name)).Select(x => x.Name).ToArray());
            var passwordProvider = new PasswordProvider(keyStoreConfig);
            
            for (var index = 0; index < test.PasswordFiles.Count; ++index)
            {
                var actualPassword = passwordProvider.GetPassword(index).Unsecure();
                var expectedPassword = test.ExpectedPasswords[index];
                Assert.AreEqual(expectedPassword, actualPassword);
            }
        }

        public class PasswordProviderTest
        {
            public string[] Passwords { get; set; } = Array.Empty<string>();
            public List<string> PasswordFiles { get; set; } = new List<string>();
            public string[] ExpectedPasswords { get; set; } = Array.Empty<string>();

            public override string ToString() => string.Join("; ", ExpectedPasswords);
        }
    }
}
