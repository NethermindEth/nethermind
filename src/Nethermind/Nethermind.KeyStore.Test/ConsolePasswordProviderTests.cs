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

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.KeyStore.ConsoleHelpers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    public class ConsolePasswordProviderTests
    {
        [Test]
        public void Alternative_provider_sets_correctly()
        {
            var emptyPasswordProvider = new FilePasswordProvider(address => string.Empty);
            var consolePasswordProvider1 = emptyPasswordProvider
                                            .OrReadFromConsole("Test1");

            Assert.IsTrue(consolePasswordProvider1 is FilePasswordProvider);
            Assert.AreEqual("Test1", ((ConsolePasswordProvider)consolePasswordProvider1.AlternativeProvider).Message);

            var consolePasswordProvider2 = consolePasswordProvider1
                                            .OrReadFromConsole("Test2");

            Assert.IsTrue(consolePasswordProvider2 is FilePasswordProvider);
            Assert.AreEqual("Test2", ((ConsolePasswordProvider)consolePasswordProvider2.AlternativeProvider).Message);
        }

        [Test]
        public void GetPassword([ValueSource(nameof(PasswordProviderTestCases))] ConsolePasswordProviderTest test)
        {
            IConsoleWrapper consoleWrapper = Substitute.For<IConsoleWrapper>();
            var chars = test.InputChars;
            var iterator = 0;
            consoleWrapper.ReadKey(true).Returns(s => { ConsoleKeyInfo key = chars[iterator];
                ++iterator;
                return key;
            });
            var passwordProvider = new ConsolePasswordProvider(new ConsoleUtils(consoleWrapper));
            var password = passwordProvider.GetPassword(Address.Zero);
            Assert.IsTrue(password.IsReadOnly());
            Assert.AreEqual(test.ExpectedPassword, password.Unsecure());
        }

        public static IEnumerable<ConsolePasswordProviderTest> PasswordProviderTestCases
        {
            get
            {
                yield return new ConsolePasswordProviderTest()
                {
                    ExpectedPassword = "T",
                    InputChars = new ConsoleKeyInfo[]
                    {
                        new ConsoleKeyInfo('T', ConsoleKey.T, false, false, false),
                        new ConsoleKeyInfo((char)13, ConsoleKey.Enter, false, false, false)
                    },
                };
                yield return new ConsolePasswordProviderTest()
                {
                    ExpectedPassword = "Asx",
                    InputChars = new ConsoleKeyInfo[]
                    {
                        new ConsoleKeyInfo('A', ConsoleKey.A, false, false, false),
                        new ConsoleKeyInfo('s', ConsoleKey.S, false, false, false),
                        new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false),
                        new ConsoleKeyInfo((char)13, ConsoleKey.Enter, false, false, false)
                    },
                };
                yield return new ConsolePasswordProviderTest()
                {
                    ExpectedPassword = "rd",
                    InputChars = new ConsoleKeyInfo[]
                    {
                        new ConsoleKeyInfo('A', ConsoleKey.A, false, false, false),
                        new ConsoleKeyInfo((char)8, ConsoleKey.Backspace, false, false, false),
                        new ConsoleKeyInfo('r', ConsoleKey.R, false, false, false),
                        new ConsoleKeyInfo('d', ConsoleKey.D, false, false, false),
                        new ConsoleKeyInfo((char)13, ConsoleKey.Enter, false, false, false)
                    },
                };
                yield return new ConsolePasswordProviderTest()
                {
                    ExpectedPassword = "po",
                    InputChars = new ConsoleKeyInfo[]
                    {
                        new ConsoleKeyInfo((char)8, ConsoleKey.Backspace, false, false, false),
                        new ConsoleKeyInfo('j', ConsoleKey.A, false, false, false),
                        new ConsoleKeyInfo((char)8, ConsoleKey.Backspace, false, false, false),
                        new ConsoleKeyInfo((char)8, ConsoleKey.Backspace, false, false, false),
                        new ConsoleKeyInfo('p', ConsoleKey.R, false, false, false),
                        new ConsoleKeyInfo('o', ConsoleKey.D, false, false, false),
                        new ConsoleKeyInfo('o', ConsoleKey.D, false, false, false),
                        new ConsoleKeyInfo((char)8, ConsoleKey.Backspace, false, false, false),
                        new ConsoleKeyInfo((char)13, ConsoleKey.Enter, false, false, false)
                    },
                };
            }
        }

        public class ConsolePasswordProviderTest
        {
            public ConsoleKeyInfo[] InputChars { get; set; } = Array.Empty<ConsoleKeyInfo>();
            public string ExpectedPassword { get; set; }

            public override string ToString() => string.Join("; ", ExpectedPassword);
        }
    }
}
