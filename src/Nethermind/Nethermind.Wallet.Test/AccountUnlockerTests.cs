// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using System.IO;

namespace Nethermind.Wallet.Test
{
    public class AccountUnlockerTests
    {
        private static List<(string Name, string Content)> _files = new List<(string Name, string Content)>()
        {
            ("TestingFileF1", "PF1"),
            ("TestingFileF2", "PF2")
        };

        private string TestDir => TestContext.CurrentContext.WorkDirectory;

        [SetUp]
        public void SetUp()
        {
            foreach ((string name, string content) in _files)
            {
                string filePath = Path.Combine(TestDir, name);
                if (!File.Exists(filePath))
                {
                    File.Create(filePath).Close();
                    File.WriteAllText(filePath, content);
                }
            }
        }
        public static IEnumerable<UnlockAccountsTest> UnlockAccountsTestCases
        {
            get
            {
                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[0].Name },
                    ExpectedPasswords = new[] { _files[0].Content, _files[0].Content },
                };

                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[0].Name, _files[1].Name },
                    ExpectedPasswords = new[] { _files[0].Content, _files[1].Content },
                };

                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    ExpectedPasswords = new[] { "A", "B" },
                };

                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    ExpectedPasswords = new[] { "A", "B" },
                };
            }
        }

        [TearDown]
        public void TearDown()
        {
            string resourcePath = TestContext.CurrentContext.TestDirectory;
            foreach ((string Name, string Content) file in _files)
            {
                string filePath = Path.Combine(resourcePath, file.Name);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        [Test]
        public void UnlockAccounts([ValueSource(nameof(UnlockAccountsTestCases))] UnlockAccountsTest test)
        {
            IKeyStoreConfig keyStoreConfig = Substitute.For<IKeyStoreConfig>();
            keyStoreConfig.Passwords.Returns(test.Passwords);
            keyStoreConfig.PasswordFiles.Returns(_files.Where(x => test.PasswordFiles.Contains(x.Name)).Select(x => x.Name).ToArray());
            keyStoreConfig.UnlockAccounts.Returns(test.UnlockAccounts.Select(a => a.ToString()).ToArray());

            IWallet wallet = Substitute.For<IWallet>();

            AccountUnlocker unlocker = new AccountUnlocker(keyStoreConfig, wallet, LimboLogs.Instance, new KeyStorePasswordProvider(keyStoreConfig));
            unlocker.UnlockAccounts();

            for (int index = 0; index < test.UnlockAccounts.Length; index++)
            {
                Address account = test.UnlockAccounts[index];
                string expectedPassword = test.ExpectedPasswords[index];
                wallet.Received(1).UnlockAccount(account, Arg.Is<SecureString>(s => s.Unsecure() == expectedPassword), TimeSpan.FromDays(1000));
            }
        }

        public class UnlockAccountsTest
        {
            public string[] Passwords { get; set; } = Array.Empty<string>();
            public List<string> PasswordFiles { get; set; } = new List<string>();
            public Address[] UnlockAccounts { get; set; } = Array.Empty<Address>();
            public string[] ExpectedPasswords { get; set; } = Array.Empty<string>();

            public override string ToString() => string.Join("; ", ExpectedPasswords);
        }
    }
}
