// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.IO;
using Nethermind.BalanceViewer.Plugin;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[TestFixture]
public class DetectionCacheTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bv-detection-" + TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public void Get_returns_null_for_unknown_account()
    {
        DetectionCache cache = new(_dir, LimboLogs.Instance);
        Assert.That(cache.Get(1, "0xabc"), Is.Null);
    }

    [Test]
    public void Put_then_Get_round_trips_and_is_case_insensitive()
    {
        DetectionCache cache = new(_dir, LimboLogs.Instance);
        DetectionEntry entry = new([new DetectedToken("0xToKeN", "PEPE", 18)], ScannedFrom: 0, Head: 100, Complete: true, UpdatedMs: 1);
        cache.Put(1, "0xABCDEF", entry);

        DetectionEntry? read = cache.Get(1, "0xabcdef");
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.Complete, Is.True);
        Assert.That(read.Tokens, Has.Count.EqualTo(1));
        Assert.That(read.Tokens[0].Symbol, Is.EqualTo("PEPE"));
    }

    [Test]
    public void Evicts_least_recently_updated_when_over_capacity()
    {
        DetectionCache cache = new(_dir, LimboLogs.Instance, maxEntries: 2);
        cache.Put(1, "0xa", new DetectionEntry([], 0, 1, true, UpdatedMs: 1)); // oldest
        cache.Put(1, "0xb", new DetectionEntry([], 0, 1, true, UpdatedMs: 2));
        cache.Put(1, "0xc", new DetectionEntry([], 0, 1, true, UpdatedMs: 3)); // pushes out 0xa

        Assert.That(cache.Get(1, "0xa"), Is.Null, "least-recently-updated entry evicted");
        Assert.That(cache.Get(1, "0xb"), Is.Not.Null);
        Assert.That(cache.Get(1, "0xc"), Is.Not.Null);
    }

    [Test]
    public void Caps_tokens_per_entry()
    {
        DetectionCache cache = new(_dir, LimboLogs.Instance, maxTokensPerEntry: 2);
        DetectedToken[] many = [new("0x1", "A", 18), new("0x2", "B", 18), new("0x3", "C", 18)];
        cache.Put(1, "0xa", new DetectionEntry(many, 0, 1, true, UpdatedMs: 1));

        Assert.That(cache.Get(1, "0xa")!.Tokens, Has.Count.EqualTo(2));
    }

    [Test]
    public void Entries_persist_across_instances()
    {
        DetectionEntry entry = new([new DetectedToken("0xtoken", "GNO", 18)], ScannedFrom: 5, Head: 200, Complete: false, UpdatedMs: 2);
        new DetectionCache(_dir, LimboLogs.Instance).Put(100, "0xdead", entry);

        // a fresh instance (as after a node restart) loads the persisted file
        DetectionCache reopened = new(_dir, LimboLogs.Instance);
        DetectionEntry? read = reopened.Get(100, "0xDEAD");
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.ScannedFrom, Is.EqualTo(5));
        Assert.That(read.Tokens[0].Symbol, Is.EqualTo("GNO"));
    }
}
