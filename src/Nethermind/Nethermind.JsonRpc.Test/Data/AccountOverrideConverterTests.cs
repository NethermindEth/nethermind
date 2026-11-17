// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data;

public class AccountOverrideConverterTests
{
    private static readonly Dictionary<Type, object> SampleValues = new()
    {
        [typeof(ulong?)] = 7UL,
        [typeof(UInt256?)] = (UInt256)9,
        [typeof(byte[])] = new byte[] { 0x60, 0x00 },
        [typeof(Address)] = TestItem.AddressA,
        [typeof(Dictionary<UInt256, Hash256>)] = new Dictionary<UInt256, Hash256> { [1] = TestItem.KeccakA },
    };

    private static IEnumerable<PropertyInfo> SerializedProperties() =>
        typeof(AccountOverride).GetProperties().Where(static p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    /// <remarks>The converter lists the fields by hand, so a property it misses would be rejected on read and dropped on write.</remarks>
    [TestCaseSource(nameof(SerializedProperties))]
    public void Converter_round_trips_every_property(PropertyInfo property)
    {
        Assert.That(SampleValues.TryGetValue(property.PropertyType, out object? sample), $"No sample value for {property.PropertyType}");
        AccountOverride original = new();
        property.SetValue(original, sample);
        EthereumJsonSerializer serializer = new();

        string json = serializer.Serialize(original);
        AccountOverride? roundTripped = serializer.Deserialize<AccountOverride>(json);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Contain($"\"{JsonNamingPolicy.CamelCase.ConvertName(property.Name)}\""));
            Assert.That(property.GetValue(roundTripped), Is.EqualTo(sample));
        }
    }
}
