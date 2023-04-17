// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Cli.Test
{
    public class SanitizeCliInputTests
    {
        [TestCase(null, "")]
        [TestCase("", "")]
        [TestCase("\t", "")]
        [TestCase(" ", "")]
        [TestCase("42", "42")]
        [TestCase("42", "42")]
        [TestCase("42\0", "42")]
        [TestCase("42\042", "42")]
        [TestCase("42\x0008", "4")]
        [TestCase("42\x0008\x0008", "")]
        [TestCase("42\x0008\x0008\x0008", "")]
        [TestCase("4\x0008\x00082", "2")]
        [TestCase("4\x00082\x0008", "")]
        public void Cli_Input_Should_Be_Properly_Sanitized(string input, string expectedOutput)
        {
            Assert.AreEqual(expectedOutput, Program.RemoveDangerousCharacters(input));
        }
    }
}
