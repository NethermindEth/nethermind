// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            Signature signature = new(0, 0, v);
            Assert.AreEqual(chainId, signature.ChainId);
        }
    }
}
