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
            ("TestingPasswordProviderFileF2", "P    F2"),
            ("TestingPasswordProviderFileF3", "P    F3    ")
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
                    ExpectedPasswords = new[] { _files[0].Content.Trim(), _files[0].Content.Trim() },
                    BlockAuthorAccount = TestItem.AddressA,
                    ExpectectedBlockAuthorAccountPassword = _files[0].Content.Trim()
                };

                yield return new PasswordProviderTest()
                {
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[0].Name, _files[1].Name },
                    ExpectedPasswords = new[] { _files[0].Content.Trim(), _files[1].Content.Trim() },
                    BlockAuthorAccount = TestItem.AddressB,
                    ExpectectedBlockAuthorAccountPassword = _files[1].Content.Trim()
                };

                yield return new PasswordProviderTest()
                {
                    UnlockAccounts = new[] { TestItem.AddressA, TestItem.AddressB },
                    Passwords = new[] { "A", "B" },
                    ExpectedPasswords = new[] { "A", "B" },
                    BlockAuthorAccount = TestItem.AddressB,
                    ExpectectedBlockAuthorAccountPassword = "B"
                };

                yield return new PasswordProviderTest()
                {
                    UnlockAccounts = new[] { TestItem.AddressA },
                    Passwords = new[] { "A", "B" },
                    PasswordFiles = new List<string> { _files[2].Name },
                    ExpectedPasswords = new[] { _files[2].Content.Trim() },
                    BlockAuthorAccount = TestItem.AddressA,
                    ExpectectedBlockAuthorAccountPassword = _files[2].Content.Trim()
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
