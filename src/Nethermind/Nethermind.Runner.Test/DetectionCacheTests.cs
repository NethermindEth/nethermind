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
