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

using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto
{
    [TestFixture]
    public class SignatureTests
    {
        [TestCase(27ul, null)]
        [TestCase(28ul, null)]
        [TestCase(35ul, 0)]
        [TestCase(36ul, 0)]
        [TestCase(37ul, 1)]
        [TestCase(38ul, 1)]
        [TestCase(35ul + 2 * 314158, 314158)]
        [TestCase(36ul + 2 * 314158, 314158)]
        public void Test(ulong v, int? chainId)
        {
            Signature signature = new Signature(0, 0, v);
            Assert.AreEqual(chainId, signature.ChainId);
        }
    }
}
