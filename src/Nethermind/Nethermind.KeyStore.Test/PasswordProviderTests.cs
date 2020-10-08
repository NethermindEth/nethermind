using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
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
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[0].Name },
                    ExpectedPasswords = new[] { _files[0].Content, _files[0].Content },
                    BlockAuthorAccount = TestItem.AddressA,
                    ExpectectedBlockAuthorAccountPassword = _files[0].Content
                };

                yield return new PasswordProviderTest()
                {
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[0].Name, _files[1].Name },
                    ExpectedPasswords = new[] { _files[0].Content, _files[1].Content },
                    BlockAuthorAccount = TestItem.AddressB,
                    ExpectectedBlockAuthorAccountPassword = _files[1].Content
                };

                yield return new PasswordProviderTest()
                {
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    ExpectedPasswords = new[] { "A", "B" },
                    BlockAuthorAccount = TestItem.AddressB,
                    ExpectectedBlockAuthorAccountPassword = "B"
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

        [Test]
        public void GetBlockAuthorPassword([ValueSource(nameof(PasswordProviderTestCases))] PasswordProviderTest test)
        {
            IKeyStoreConfig keyStoreConfig = Substitute.For<IKeyStoreConfig>();
            keyStoreConfig.Passwords.Returns(test.Passwords);
            keyStoreConfig.PasswordFiles.Returns(_files.Where(x => test.PasswordFiles.Contains(x.Name)).Select(x => x.Name).ToArray());
            keyStoreConfig.BlockAuthorAccount.Returns(test.BlockAuthorAccount.ToString());
            keyStoreConfig.UnlockAccounts.Returns(test.UnlockAccounts.Select(a => a.ToString()).ToArray());
            var passwordProvider = new PasswordProvider(keyStoreConfig);
            var blockAuthorPassword = passwordProvider.GetBlockAuthorPassword().Unsecure();
            Assert.AreEqual(test.ExpectectedBlockAuthorAccountPassword, blockAuthorPassword);
        }
    }

    public class PasswordProviderTest
    {
        public string[] Passwords { get; set; } = Array.Empty<string>();
        public List<string> PasswordFiles { get; set; } = new List<string>();
        public string[] ExpectedPasswords { get; set; } = Array.Empty<string>();
        public Address[] UnlockAccounts { get; set; } = Array.Empty<Address>();
        public Address BlockAuthorAccount { get; set; }
        public string ExpectectedBlockAuthorAccountPassword { get; set; }

        public override string ToString() => string.Join("; ", ExpectedPasswords);
    }
}
