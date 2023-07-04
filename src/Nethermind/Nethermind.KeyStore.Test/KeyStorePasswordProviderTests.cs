// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    public class KeyStorePasswordProviderTests
    {
        private static List<(string Name, string Content)> _files = new List<(string Name, string Content)>()
        {
            ("TestingPasswordProviderFileF1", "PF1"),
            ("TestingPasswordProviderFileF2", "P    F2"),
            ("TestingPasswordProviderFileF3", "   P    F3    ")
        };

        private string TestDir => TestContext.CurrentContext.WorkDirectory;

        [SetUp]
        public void SetUp()
        {
            foreach (var file in _files)
            {
                var filePath = Path.Combine(TestDir, file.Name);
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
            foreach (var file in _files)
            {
                var filePath = Path.Combine(TestDir, file.Name);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        public static IEnumerable<KeyStorePasswordProviderTest> PasswordProviderTestCases
        {
            get
            {
                yield return new KeyStorePasswordProviderTest()
                {
                    TestName = "A B both from same file",
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[0].Name },
                    ExpectedPasswords = new[] { _files[0].Content.Trim(), _files[0].Content.Trim() },
                    BlockAuthorAccount = TestItem.AddressA,
                    ExpectedBlockAuthorAccountPassword = _files[0].Content.Trim()
                };

                yield return new KeyStorePasswordProviderTest()
                {
                    TestName = "A B two different files",
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[0].Name, _files[1].Name },
                    ExpectedPasswords = new[] { _files[0].Content.Trim(), _files[1].Content.Trim() },
                    BlockAuthorAccount = TestItem.AddressB,
                    ExpectedBlockAuthorAccountPassword = _files[1].Content.Trim()
                };

                yield return new KeyStorePasswordProviderTest()
                {
                    TestName = "A B from password directly",
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    ExpectedPasswords = new[] { "A", "B" },
                    BlockAuthorAccount = TestItem.AddressB,
                    ExpectedBlockAuthorAccountPassword = "B"
                };

                yield return new KeyStorePasswordProviderTest()
                {
                    TestName = "A B from same file but file needs to be trimmed",
                    UnlockAccounts = new[] { TestItem.AddressA },
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[2].Name },
                    ExpectedPasswords = new[] { _files[2].Content.Trim() },
                    BlockAuthorAccount = TestItem.AddressA,
                    ExpectedBlockAuthorAccountPassword = _files[2].Content.Trim()
                };
            }
        }

        [Test]
        public void GetPassword([ValueSource(nameof(PasswordProviderTestCases))] KeyStorePasswordProviderTest test)
        {
            IKeyStoreConfig keyStoreConfig = Substitute.For<IKeyStoreConfig>();
            keyStoreConfig.Passwords.Returns(test.Passwords);
            keyStoreConfig.UnlockAccounts.Returns(test.UnlockAccounts.Select(a => a.ToString()).ToArray());
            keyStoreConfig.PasswordFiles.Returns(_files.Where(x => test.PasswordFiles.Contains(x.Name)).Select(x => x.Name).ToArray());
            var passwordProvider = new KeyStorePasswordProvider(keyStoreConfig);

            for (var index = 0; index < test.PasswordFiles.Count; ++index)
            {
                var actualPassword = passwordProvider.GetPassword(test.UnlockAccounts[index]);
                var expectedPassword = test.ExpectedPasswords[index];
                Assert.IsTrue(actualPassword.IsReadOnly());
                Assert.That(actualPassword.Unsecure(), Is.EqualTo(expectedPassword));
            }
        }

        [Test]
        public void GetBlockAuthorPassword([ValueSource(nameof(PasswordProviderTestCases))] KeyStorePasswordProviderTest test)
        {
            IKeyStoreConfig keyStoreConfig = Substitute.For<IKeyStoreConfig>();
            keyStoreConfig.Passwords.Returns(test.Passwords);
            keyStoreConfig.PasswordFiles.Returns(_files.Where(x => test.PasswordFiles.Contains(x.Name)).Select(x => x.Name).ToArray());
            keyStoreConfig.BlockAuthorAccount.Returns(test.BlockAuthorAccount.ToString());
            keyStoreConfig.UnlockAccounts.Returns(test.UnlockAccounts.Select(a => a.ToString()).ToArray());
            var passwordProvider = new KeyStorePasswordProvider(keyStoreConfig);
            var blockAuthorPassword = passwordProvider.GetPassword(new Address(Bytes.FromHexString(keyStoreConfig.BlockAuthorAccount)));
            Assert.IsTrue(blockAuthorPassword.IsReadOnly());
            blockAuthorPassword.Unsecure().Should().Be(test.ExpectedBlockAuthorAccountPassword, test.TestName);
        }
    }

    public class KeyStorePasswordProviderTest
    {
        public string TestName { get; set; } = string.Empty;
        public string[] Passwords { get; set; } = Array.Empty<string>();
        public List<string> PasswordFiles { get; set; } = new List<string>();
        public string[] ExpectedPasswords { get; set; } = Array.Empty<string>();
        public Address[] UnlockAccounts { get; set; } = Array.Empty<Address>();
        public Address BlockAuthorAccount { get; set; }
        public string ExpectedBlockAuthorAccountPassword { get; set; }

        public override string ToString() => TestName + " " + string.Join("; ", ExpectedPasswords);
    }
}
