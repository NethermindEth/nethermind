// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Verkle.Curve;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class BanderwagonConverterTests : ConverterTestBase<Banderwagon>
{
    [Test]
    public void TestRoundTrip()
    {
        Banderwagon item =
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x0ef3fcb96d17a16ee996440fc5bedcb6a82b4ccf7b8b9243228b7bb422f5715b"), true)!.Value;
        var options = new JsonSerializerOptions
        {
            Converters =
            {
                new BanderwagonConverter()
            }
        };

        string results = JsonSerializer.Serialize(item, options);
        Banderwagon res = JsonSerializer.Deserialize<Banderwagon>(results, options);

        Assert.IsTrue(item == res);
    }
}
