// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class GCSchedulerTests
{
    [TestCase(GCCollectionMode.Forced, true, false, ExpectedResult = GCCollectionMode.Forced)]
    [TestCase(GCCollectionMode.Aggressive, true, true, ExpectedResult = GCCollectionMode.Forced)]
    public GCCollectionMode Blocking_collection_downgrades_to_background_when_processing_recently(
        GCCollectionMode mode, bool blocking, bool compacting)
    {
        (GCCollectionMode resolvedMode, bool resolvedBlocking, bool resolvedCompacting) =
            GCScheduler.ResolveGCSeverity(mode, blocking, compacting, isProcessingRecently: true);

        Assert.That(resolvedBlocking, Is.False);
        Assert.That(resolvedCompacting, Is.False);
        return resolvedMode;
    }

    [TestCase(GCCollectionMode.Forced, true, false)]
    [TestCase(GCCollectionMode.Aggressive, true, true)]
    [TestCase(GCCollectionMode.Forced, false, false)]
    public void Collection_is_unchanged_when_idle(GCCollectionMode mode, bool blocking, bool compacting) =>
        Assert.That(
            GCScheduler.ResolveGCSeverity(mode, blocking, compacting, isProcessingRecently: false),
            Is.EqualTo((mode, blocking, compacting)));

    [Test]
    public void Non_blocking_collection_is_unchanged_even_when_processing_recently() =>
        Assert.That(
            GCScheduler.ResolveGCSeverity(GCCollectionMode.Forced, blocking: false, compacting: false, isProcessingRecently: true),
            Is.EqualTo((GCCollectionMode.Forced, false, false)));
}
