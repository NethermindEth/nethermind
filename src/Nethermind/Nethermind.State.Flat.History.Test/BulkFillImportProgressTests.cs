// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class BulkFillImportProgressTests
{
    [TestCase(0, 0.5, 60, TestName = "ImportEta_FromStart_UsesObservedProgress")]
    [TestCase(0.5, 0.75, 60, TestName = "ImportEta_AfterResume_ExcludesPriorProgress")]
    public void ImportEta_WhenProgressAdvances_EstimatesRemaining(double initial, double current, double expectedSeconds) =>
        Assert.That(BulkFillImportProgress.Remaining(initial, current, TimeSpan.FromSeconds(60), false),
            Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));

    [TestCase(0, TestName = "ImportEta_WithoutProgress_IsUnknown")]
    [TestCase(0.5, TestName = "ImportEta_WithoutProgressAfterResume_IsUnknown")]
    public void ImportEta_WhenProgressDoesNotAdvance_IsUnknown(double fraction) =>
        Assert.That(BulkFillImportProgress.Remaining(fraction, fraction, TimeSpan.FromSeconds(60), false), Is.Null);

    [Test]
    public void ImportProgress_WhenKeyspaceEnds_DoesNotReportCompleteBeforeEof()
    {
        double fraction = BulkFillImportProgress.Fraction(new byte[] { 0xff, 0xff, 0xff, 0xff });
        Assert.That(fraction, Is.LessThan(1));
        string report = BulkFillImportProgress.Format(FlatHistoryColumns.AccountHistory, 1, 8192, 0,
            fraction, TimeSpan.FromSeconds(60), false, 0);
        Assert.That(report, Does.Contain("99.99"));
        Assert.That(report, Does.Contain("rows this run"));
        Assert.That(report, Does.Contain("keyspace-based"));
        Assert.That(report, Does.Contain("SST/blob"));
    }

    [Test]
    public void ImportEta_WhenComplete_IsZeroEvenForEmptyColumn()
    {
        Assert.That(BulkFillImportProgress.Remaining(0, 0, TimeSpan.Zero, true), Is.EqualTo(TimeSpan.Zero));
        Assert.That(BulkFillImportProgress.Fraction([]), Is.Zero);
    }
}
