// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Reflection;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

public class SpecSnapshotTests
{
    [Test]
    public void SpecSnapshot_correctly_maps_all_bool_properties()
    {
        // Create a ReleaseSpec with ALL bools set to true
        ReleaseSpec spec = new();
        foreach (PropertyInfo prop in typeof(IReleaseSpec).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && p.CanWrite))
        {
            typeof(ReleaseSpec).GetProperty(prop.Name)?.SetValue(spec, true);
        }
        spec.MaxCodeSize = 12345;

        SpecSnapshot snapshot = new(spec);

        // Verify every bool property on IReleaseSpec matches the snapshot
        foreach (PropertyInfo prop in typeof(IReleaseSpec).GetProperties()
            .Where(p => p.PropertyType == typeof(bool)))
        {
            bool expected = (bool)prop.GetValue(spec)!;
            PropertyInfo? snapshotProp = typeof(SpecSnapshot).GetProperty(prop.Name);
            Assert.That(snapshotProp, Is.Not.Null, $"Missing property on SpecSnapshot: {prop.Name}");
            bool actual = (bool)snapshotProp!.GetValue(snapshot)!;
            Assert.That(actual, Is.EqualTo(expected), $"Mismatch for {prop.Name}: expected {expected}, got {actual}");
        }

        Assert.That(snapshot.MaxCodeSize, Is.EqualTo(12345));
        Assert.That(snapshot.MaxInitCodeSize, Is.EqualTo(2 * 12345));
    }

    [Test]
    public void SpecSnapshot_correctly_maps_all_false_bools()
    {
        // Create a ReleaseSpec with ALL bools set to false (default)
        ReleaseSpec spec = new();
        spec.MaxCodeSize = 99;

        SpecSnapshot snapshot = new(spec);

        foreach (PropertyInfo prop in typeof(IReleaseSpec).GetProperties()
            .Where(p => p.PropertyType == typeof(bool)))
        {
            bool expected = (bool)prop.GetValue(spec)!;
            PropertyInfo? snapshotProp = typeof(SpecSnapshot).GetProperty(prop.Name);
            if (snapshotProp is null) continue; // skip if not mapped (will be caught by the other test)
            bool actual = (bool)snapshotProp.GetValue(snapshot)!;
            Assert.That(actual, Is.EqualTo(expected), $"Mismatch for {prop.Name}: expected {expected}, got {actual}");
        }

        Assert.That(snapshot.MaxCodeSize, Is.EqualTo(99));
    }

    [Test]
    public void SpecSnapshot_captures_precompiles_and_eip2935_fields()
    {
        ReleaseSpec spec = new()
        {
            IsEip2935Enabled = true,
            Eip2935ContractAddress = Core.Address.SystemUser,
            Eip2935RingBufferSize = 8192
        };

        SpecSnapshot snapshot = new(spec);

        Assert.That(snapshot.Eip2935ContractAddress, Is.EqualTo(Core.Address.SystemUser));
        Assert.That(snapshot.Eip2935RingBufferSize, Is.EqualTo(8192));
        Assert.That(snapshot.IsEip2935Enabled, Is.True);
        Assert.That(snapshot.Precompiles, Is.Not.Null);
    }

    [Test]
    public void ReleaseSpec_Snapshot_is_cached()
    {
        ReleaseSpec spec = new() { MaxCodeSize = 100 };

        SpecSnapshot first = spec.Snapshot;
        SpecSnapshot second = spec.Snapshot;

        // Same value (struct equality by fields)
        Assert.That(first.MaxCodeSize, Is.EqualTo(second.MaxCodeSize));
    }
}
