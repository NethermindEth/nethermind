// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Ethereum.Test.Base;

/// <summary>Fixture files that cannot be loaded yet, reported as skipped with the feature they need.</summary>
/// <remarks>
/// A file is skipped only while it fails with the error listed for it. Once it loads, its cases run again, and any
/// other error fails like every other load failure. Each file is listed by its full path under <c>src/tests</c>, so a
/// new file in the same directory is not covered.
/// </remarks>
internal static class QuarantinedFixtures
{
    private const string TotalDifficultyTransition =
        "the blockchain test harness does not support a fork transition at a total difficulty (an 'AtDiff' network)";

    private const string OverWideTransactionValue =
        "the state test reader does not accept a transaction value wider than 256 bits";

    private static readonly QuarantinedFile[] Files =
    [
        new("LegacyTests/Cancun/BlockchainTests/TransitionTests/bcArrowGlacierToParis/difficultyFormula.json",
            TotalDifficultyTransition, ["System.FormatException", "'DiffC0000'"]),
        new("LegacyTests/Cancun/BlockchainTests/TransitionTests/bcArrowGlacierToParis/powToPosBlockRejection.json",
            TotalDifficultyTransition, ["System.FormatException", "'DiffC0000'"]),
        new("LegacyTests/Cancun/BlockchainTests/TransitionTests/bcArrowGlacierToParis/powToPosTest.json",
            TotalDifficultyTransition, ["System.FormatException", "'DiffC0000'"]),
        new("LegacyTests/Cancun/GeneralStateTests/stTransactionTest/ValueOverflow.json",
            OverWideTransactionValue, ["System.Text.Json.JsonException", "Nethermind.Int256.UInt256", "$.ValueOverflow.transaction.value["]),
        new("LegacyTests/Cancun/GeneralStateTests/stTransactionTest/ValueOverflowParis.json",
            OverWideTransactionValue, ["System.Text.Json.JsonException", "Nethermind.Int256.UInt256", "$.ValueOverflowParis.transaction.value["]),
    ];

    /// <summary>Skips <paramref name="test"/> when it is a listed file that failed to load with its listed error.</summary>
    /// <exception cref="IgnoreException">The test is a quarantined file; the message holds the reason, the file and the load error.</exception>
    public static void IgnoreIfQuarantined(EthereumTest test)
    {
        if (test.LoadFailure is not { } error || test.Name is not { } name)
            return;

        string path = name.Replace('\\', '/');
        foreach (QuarantinedFile file in Files)
        {
            if (path.EndsWith('/' + file.Path, StringComparison.Ordinal) && file.FailsWith(error))
                Assert.Ignore($"Quarantined: {file.Reason}. File: {name}. Load error: {error}");
        }
    }

    private sealed record QuarantinedFile(string Path, string Reason, string[] ErrorSignature)
    {
        public bool FailsWith(string error)
        {
            foreach (string part in ErrorSignature)
            {
                if (!error.Contains(part, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }
}
