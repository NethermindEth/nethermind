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
