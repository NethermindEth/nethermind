// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.KeyStore.ConsoleHelpers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test;

public class ConsolePasswordProviderTests
{
    [Test]
    public void Alternative_provider_sets_correctly()
    {
        FilePasswordProvider emptyPasswordProvider = new(static address => string.Empty);
        BasePasswordProvider consolePasswordProvider1 = emptyPasswordProvider
                                        .OrReadFromConsole("Test1");

        Assert.That(consolePasswordProvider1 is FilePasswordProvider, Is.True);
        Assert.That(((ConsolePasswordProvider)consolePasswordProvider1.AlternativeProvider).Message, Is.EqualTo("Test1"));

        BasePasswordProvider consolePasswordProvider2 = consolePasswordProvider1
                                        .OrReadFromConsole("Test2");

        Assert.That(consolePasswordProvider2 is FilePasswordProvider, Is.True);
        Assert.That(((ConsolePasswordProvider)consolePasswordProvider2.AlternativeProvider).Message, Is.EqualTo("Test2"));
    }

    [Test]
    public void GetPassword([ValueSource(nameof(PasswordProviderTestCases))] ConsolePasswordProviderTest test)
    {
        IConsoleWrapper consoleWrapper = Substitute.For<IConsoleWrapper>();
        ConsoleKeyInfo[] chars = test.InputChars;
        int iterator = 0;
        consoleWrapper.ReadKey(true).Returns(s =>
        {
            ConsoleKeyInfo key = chars[iterator];
            ++iterator;
            return key;
        });
        ConsolePasswordProvider passwordProvider = new(new ConsoleUtils(consoleWrapper));
        SecureString password = passwordProvider.GetPassword(Address.Zero);
        Assert.That(password.IsReadOnly(), Is.True);
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
                    new('T', ConsoleKey.T, false, false, false),
                    new((char)13, ConsoleKey.Enter, false, false, false)
                },
            };
            yield return new ConsolePasswordProviderTest()
            {
                ExpectedPassword = "Asx",
                InputChars = new ConsoleKeyInfo[]
                {
                    new('A', ConsoleKey.A, false, false, false),
                    new('s', ConsoleKey.S, false, false, false),
                    new('x', ConsoleKey.X, false, false, false),
                    new((char)13, ConsoleKey.Enter, false, false, false)
                },
            };
            yield return new ConsolePasswordProviderTest()
            {
                ExpectedPassword = "rd",
                InputChars = new ConsoleKeyInfo[]
                {
                    new('A', ConsoleKey.A, false, false, false),
                    new((char)8, ConsoleKey.Backspace, false, false, false),
                    new('r', ConsoleKey.R, false, false, false),
                    new('d', ConsoleKey.D, false, false, false),
                    new((char)13, ConsoleKey.Enter, false, false, false)
                },
            };
            yield return new ConsolePasswordProviderTest()
            {
                ExpectedPassword = "po",
                InputChars = new ConsoleKeyInfo[]
                {
                    new((char)8, ConsoleKey.Backspace, false, false, false),
                    new('j', ConsoleKey.A, false, false, false),
                    new((char)8, ConsoleKey.Backspace, false, false, false),
                    new((char)8, ConsoleKey.Backspace, false, false, false),
                    new('p', ConsoleKey.R, false, false, false),
                    new('o', ConsoleKey.D, false, false, false),
                    new('o', ConsoleKey.D, false, false, false),
                    new((char)8, ConsoleKey.Backspace, false, false, false),
                    new((char)13, ConsoleKey.Enter, false, false, false)
                },
            };
        }
    }

    public class ConsolePasswordProviderTest
    {
        public ConsoleKeyInfo[] InputChars { get; set; } = [];
        public string ExpectedPassword { get; set; }

        public override string ToString() => string.Join("; ", ExpectedPassword);
    }
}
