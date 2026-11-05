// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Nethermind.Config.Test;

[TestFixture]
public class JsonConfigSourceParseNumberTests
{
    [Test]
    public void GetRawValue_DoubleNumberInJson_RoundTripsAsDouble()
    {
        // Regression test: previously the double branch of ParseNumber returned
        // the Int64 'result' (always 0 because TryGetInt64 had failed) instead
        // of the parsed double. Sketch double config values such as
        // SketchMaxError and SketchMinConfidence were being read as "0".
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "SampleConfig":
                  {
                    "Tiny": 0.001,
                    "Big": 1.5e10
                  }
                }
                """);

            JsonConfigSource source = new(path);

            (bool isSetTiny, string? tiny) = source.GetRawValue("SampleConfig", "Tiny");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(isSetTiny, Is.True);
                Assert.That(double.Parse(tiny!, System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(0.001));
            }

            (bool isSetBig, string? big) = source.GetRawValue("SampleConfig", "Big");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(isSetBig, Is.True);
                Assert.That(double.Parse(big!, System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(1.5e10));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestCase(typeof(int), "42", 42)]
    [TestCase(typeof(long), "-9223372036854775808", long.MinValue)]
    [TestCase(typeof(ulong), "10000000000000000000", 10_000_000_000_000_000_000UL)]
    [TestCase(typeof(ulong), "18446744073709551615", ulong.MaxValue)]
    [TestCase(typeof(string), "58750000000000000000000", "58750000000000000000000")]
    public void GetValue_IntegerInJson_KeepsExactDigits(Type valueType, string number, object expected)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes($$"""{ "SampleConfig": { "Value": {{number}} } }"""));
        JsonConfigSource source = new(stream);

        Assert.That(source.GetValue(valueType, "SampleConfig", "Value").Value, Is.EqualTo(expected));
    }
}
