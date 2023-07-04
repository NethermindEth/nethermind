// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class BloomConverterTests : ConverterTestBase<Bloom>
    {
        [Test]
        public void Null_values()
        {
            TestConverter(null!, (bloom, bloom1) => bloom == bloom1, new BloomConverter());
        }

        [Test]
        public void Empty_bloom()
        {
            TestConverter(Bloom.Empty, (bloom, bloom1) => bloom.Equals(bloom1), new BloomConverter());
        }

        [Test]
        public void Full_bloom()
        {
            TestConverter(
                new Bloom(Enumerable.Range(0, 255).Select(i => (byte)i).ToArray()),
                (bloom, bloom1) => bloom.Equals(bloom1), new BloomConverter());
        }
    }
}
