// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text.Json;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

public class IPAddressConverterTests
{
    [TestCase("127.0.0.1")]
    [TestCase("::1")]
    [TestCase("2001:db8::1")]
    public void Can_roundtrip_ip_address(string address)
    {
        IPAddress ipAddress = IPAddress.Parse(address);

        string json = JsonSerializer.Serialize(ipAddress, Options);
        IPAddress? result = JsonSerializer.Deserialize<IPAddress>(json, Options);

        Assert.That(result, Is.EqualTo(ipAddress));
    }

    [Test]
    public void Can_roundtrip_null_ip_address()
    {
        string json = JsonSerializer.Serialize<IPAddress?>(null, Options);
        IPAddress? result = JsonSerializer.Deserialize<IPAddress?>(json, Options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Is.EqualTo("null"));
            Assert.That(result, Is.Null);
        }
    }

    [Test]
    public void Throws_json_exception_for_invalid_ip_address()
    {
        JsonException? exception = Assert.Throws<JsonException>(
            static () => JsonSerializer.Deserialize<IPAddress>("\"not-an-ip\"", Options));

        Assert.That(exception?.Message, Does.Contain("Invalid IP address format."));
    }

    [Test]
    public void Ethereum_serializer_writes_ip_address_as_string()
    {
        EthereumJsonSerializer serializer = new();

        string json = serializer.Serialize(new AddressContainer(IPAddress.Loopback));

        Assert.That(json, Is.EqualTo("""{"ip":"127.0.0.1"}"""));
    }

    private sealed class AddressContainer(IPAddress ip)
    {
        public IPAddress Ip { get; } = ip;
    }

    private static JsonSerializerOptions Options { get; } = new()
    {
        Converters =
        {
            new IPAddressConverter()
        }
    };
}
