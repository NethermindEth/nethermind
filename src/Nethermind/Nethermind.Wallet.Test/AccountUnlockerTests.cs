//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
                    UnlockAccounts = new [] {TestItem.AddressA, TestItem.AddressB},
                    Passwords = new []{"A", "B"},
                    PasswordFiles = new List<string> { _files[0].Name },
                    ExpectedPasswords = new []{ _files[0].Content, _files[0].Content }, 
                };

                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new [] {TestItem.AddressA, TestItem.AddressB},
                    Passwords = new []{"A", "B"},
                    PasswordFiles = new List<string> { _files[0].Name, _files[1].Name },
                    ExpectedPasswords = new []{ _files[0].Content, _files[1].Content }, 
                };
                
                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new [] {TestItem.AddressA, TestItem.AddressB},
                    Passwords = new []{"A", "B"},
                    ExpectedPasswords = new []{"A", "B"}, 
                };
                
                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new [] {TestItem.AddressA, TestItem.AddressB},
                    Passwords = new []{"A", "B"},
                    ExpectedPasswords = new []{"A", "B"}, 
                };
            }
        }

        [TearDown]
        public void TearDown()
        {
            string resourcePath = TestContext.CurrentContext.TestDirectory;
            foreach (var file in _files)
            {
                var filePath = Path.Combine(resourcePath, file.Name);
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
            
            var unlocker = new AccountUnlocker(keyStoreConfig, wallet, LimboLogs.Instance, new KeyStorePasswordProvider(keyStoreConfig));
            unlocker.UnlockAccounts();

            for (var index = 0; index < test.UnlockAccounts.Length; index++)
            {
                var account = test.UnlockAccounts[index];
                var expectedPassword = test.ExpectedPasswords[index];
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
