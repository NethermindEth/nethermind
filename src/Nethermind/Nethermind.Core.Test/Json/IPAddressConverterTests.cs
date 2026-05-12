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
    public void Can_roundtrip_ip_address(string address)
    {
        IPAddress ipAddress = IPAddress.Parse(address);
        JsonSerializerOptions options = new()
        {
            Converters =
            {
                new IPAddressConverter()
            }
        };

        string json = JsonSerializer.Serialize(ipAddress, options);
        IPAddress? result = JsonSerializer.Deserialize<IPAddress>(json, options);

        Assert.That(result, Is.EqualTo(ipAddress));
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
}
