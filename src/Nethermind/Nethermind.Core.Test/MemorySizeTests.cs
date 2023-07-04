// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class MemorySizeTests
    {
        [TestCase(1, 8)]
        [TestCase(1023, 1024)]
        public void Span(int unaligned, int aligned)
        {
            Assert.That(MemorySizes.Align(unaligned), Is.EqualTo(aligned));
        }
    }
}
