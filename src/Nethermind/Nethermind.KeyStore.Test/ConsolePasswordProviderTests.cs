// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            Assert.That(((ConsolePasswordProvider)consolePasswordProvider1.AlternativeProvider).Message, Is.EqualTo("Test1"));

            var consolePasswordProvider2 = consolePasswordProvider1
                                            .OrReadFromConsole("Test2");

            Assert.IsTrue(consolePasswordProvider2 is FilePasswordProvider);
            Assert.That(((ConsolePasswordProvider)consolePasswordProvider2.AlternativeProvider).Message, Is.EqualTo("Test2"));
        }

        [Test]
        public void GetPassword([ValueSource(nameof(PasswordProviderTestCases))] ConsolePasswordProviderTest test)
        {
            IConsoleWrapper consoleWrapper = Substitute.For<IConsoleWrapper>();
            var chars = test.InputChars;
            var iterator = 0;
            consoleWrapper.ReadKey(true).Returns(s =>
            {
                ConsoleKeyInfo key = chars[iterator];
                ++iterator;
                return key;
            });
            var passwordProvider = new ConsolePasswordProvider(new ConsoleUtils(consoleWrapper));
            var password = passwordProvider.GetPassword(Address.Zero);
            Assert.IsTrue(password.IsReadOnly());
            Assert.That(password.Unsecure(), Is.EqualTo(test.ExpectedPassword));
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
