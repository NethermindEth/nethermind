// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class UnitTests
    {
        [Test]
        public void Ratios_are_correct()
        {
            Assert.That(Unit.Finney * 1000, Is.EqualTo(Unit.Ether));
            Assert.That(Unit.Szabo * 1000 * 1000, Is.EqualTo(Unit.Ether));
            Assert.That(Unit.Wei * 1000 * 1000 * 1000 * 1000 * 1000 * 1000, Is.EqualTo(Unit.Ether));
        }
    }
}
