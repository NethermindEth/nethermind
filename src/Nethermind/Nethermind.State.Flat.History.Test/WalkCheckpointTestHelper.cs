// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.State.Flat.History.Proofs;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

internal static class WalkCheckpointTestHelper
{
    // Persisted CommitmentMetadata.WalkModeKey encoding; keep legacy fixtures independent of production helpers.
    private static ReadOnlySpan<byte> ModeKey => [0xFE, 0x0D];

    public static void RemoveMode(IColumnsDb<FlatHistoryColumns> history, CommitmentMetadata metadata)
    {
        Assert.That(metadata.WalkModeMatches(true), Is.True, "precondition: the stamped build checkpoint would otherwise be reusable");
        history.GetColumnDb(FlatHistoryColumns.AccountCommitments).Remove(ModeKey);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.WalkModeMatches(true), Is.False, "legacy mode must not match a build");
            Assert.That(metadata.WalkModeMatches(false), Is.False, "legacy mode must not match verification either");
        }
    }
}
