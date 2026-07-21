// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.PortfolioViewer.Plugin.Test;

[TestFixture]
public class PortfolioViewerPinStoreTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bv-pins-" + TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Test]
    public void Add_TracksOnlyDistinctCids_AndPersistsAcrossInstances()
    {
        PinnedCidStore store = new(_dir, LimboLogs.Instance);
        store.Add("cidA");
        store.Add("cidA"); // duplicate is a no-op
        store.Add("cidB");

        Assert.That(store.Snapshot(), Is.EquivalentTo(new[] { "cidA", "cidB" }));

        // a fresh instance rehydrates from disk, so unpin-all after a restart still removes exactly our pins
        PinnedCidStore reloaded = new(_dir, LimboLogs.Instance);
        Assert.That(reloaded.Snapshot(), Is.EquivalentTo(new[] { "cidA", "cidB" }));
    }

    [Test]
    public void Clear_ForgetsEverything_AndRemovesBackingFile()
    {
        PinnedCidStore store = new(_dir, LimboLogs.Instance);
        store.Add("cidA");
        store.Clear();

        Assert.That(store.Snapshot(), Is.Empty);
        Assert.That(new PinnedCidStore(_dir, LimboLogs.Instance).Snapshot(), Is.Empty);
    }
}
