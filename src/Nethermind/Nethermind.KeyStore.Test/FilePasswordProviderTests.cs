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

using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    public class FilePasswordProviderTests
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

        [Test]
        public void GetPassword([ValueSource(nameof(PasswordProviderTestCases))]
            FilePasswordProviderTest test)
        {
            var passwordProvider = new FilePasswordProvider(address => Path.Combine(TestDir, test.FileName));

            var password = passwordProvider.GetPassword(Address.Zero);
            Assert.IsTrue(password.IsReadOnly());
            Assert.AreEqual(test.ExpectedPassword, password.Unsecure());
        }

        [Test]
        public void Return_null_when_file_not_exists()
        {
            var passwordProvider = new FilePasswordProvider(address =>
            {
                if (address == Address.Zero)
                {
                    return string.Empty;
                }
                else
                {
                    return "NotExistingFile";
                }
            });

            var password = passwordProvider.GetPassword(Address.Zero);
            Assert.AreEqual(null, password);
            password = passwordProvider.GetPassword(Address.Zero);
            Assert.AreEqual(null, password);
        }

        [Test]
        public void Correctly_use_alternative_provider()
        {
            var passwordProvider = new FilePasswordProvider(a => string.Empty)
                .OrReadFromFile(Path.Combine(TestDir, _files[0].Name));

            var password = passwordProvider.GetPassword(Address.Zero);
            Assert.IsTrue(password.IsReadOnly());
            Assert.AreEqual(_files[0].Content.Trim(), password.Unsecure());
        }

        public static IEnumerable<FilePasswordProviderTest> PasswordProviderTestCases
        {
            get
            {
                yield return new FilePasswordProviderTest()
                {
                    FileName = _files[0].Name,
                    ExpectedPassword = _files[0].Content.Trim()
                };

                yield return new FilePasswordProviderTest()
                {
                    FileName = _files[1].Name,
                    ExpectedPassword = _files[1].Content.Trim()
                };

                yield return new FilePasswordProviderTest()
                {
                    FileName = _files[2].Name,
                    ExpectedPassword = _files[2].Content.Trim()
                };
            }
        }

        public class FilePasswordProviderTest
        {
            public string FileName { get; set; }
            public string ExpectedPassword { get; set; }

            public override string ToString() => string.Join("; ", ExpectedPassword);
        }
    }
}
