//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.IO.Abstractions;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;

namespace Nethermind.Wallet.Test
{
    public class AccountUnlockerTests
    {
        public static IEnumerable<UnlockAccountsTest> UnlockAccountsTestCases
        {
            get
            {
                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new [] {TestItem.AddressA, TestItem.AddressB},
                    Passwords = new []{"A", "B"},
                    PasswordFiles = new []{new UnlockAccountsTest.PasswordFile() {Name = "./F1", Content = "PF1", Exists = true}},
                    ExpectedPasswords = new []{"PF1", "PF1"}, 
                };

                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new [] {TestItem.AddressA, TestItem.AddressB},
                    Passwords = new []{"A", "B"},
                    PasswordFiles = new []
                    {
                        new UnlockAccountsTest.PasswordFile() {Name = "./F1", Content = "PF1", Exists = true},
                        new UnlockAccountsTest.PasswordFile() {Name = "./F2", Content = "PF2", Exists = true},
                    },
                    ExpectedPasswords = new []{"PF1", "PF2"}, 
                };
                
                yield return new UnlockAccountsTest()
                {
                    UnlockAccounts = new [] {TestItem.AddressA, TestItem.AddressB},
                    Passwords = new []{"A", "B"},
                    PasswordFiles = new []
                    {
                        new UnlockAccountsTest.PasswordFile() {Name = "./F1", Content = "PF1", Exists = false},
                        new UnlockAccountsTest.PasswordFile() {Name = "./F2", Content = "PF2", Exists = false},
                    },
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
        
        [Test]
        public void UnlockAccounts([ValueSource(nameof(UnlockAccountsTestCases))] UnlockAccountsTest test)
        {
            IKeyStoreConfig keyStoreConfig = Substitute.For<IKeyStoreConfig>();
            keyStoreConfig.Passwords.Returns(test.Passwords);
            keyStoreConfig.PasswordFiles.Returns(test.PasswordFiles.Select(f => f.Name).ToArray());
            keyStoreConfig.UnlockAccounts.Returns(test.UnlockAccounts.Select(a => a.ToString()).ToArray());
            
            IFileSystem fileSystem = Substitute.For<IFileSystem>();
            foreach (var passwordFile in test.PasswordFiles)
            {
                fileSystem.File.Exists(passwordFile.Name).Returns(passwordFile.Exists);
                fileSystem.File.ReadAllText(passwordFile.Name).Returns(passwordFile.Content);
            }
            
            IWallet wallet = Substitute.For<IWallet>();
            
            var unlocker = new AccountUnlocker(keyStoreConfig, wallet, fileSystem, LimboLogs.Instance);
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
            public PasswordFile[] PasswordFiles { get; set; } = Array.Empty<PasswordFile>();
            public Address[] UnlockAccounts { get; set; } = Array.Empty<Address>();
            public string[] ExpectedPasswords { get; set; } = Array.Empty<string>();

            public class PasswordFile
            {
                public string Name { get; set; }
                public string Content { get; set; }
                public bool Exists { get; set; }
            }
            
            public override string ToString() => string.Join("; ", ExpectedPasswords);
        }
    }
}
