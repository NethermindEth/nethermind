// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using NUnit.Framework;

namespace Ethereum.Test.Base;

/// <summary>Fixture files that cannot be loaded yet, reported as skipped with the feature they need.</summary>
/// <remarks>
/// A file is skipped only while it fails with the error listed for it. Where that error does not identify the cause on
/// its own, the input the reader stopped at must be the unsupported one too. Once the file loads, its cases run again,
/// and any other failure fails like every other load failure. Each file is listed by its full path under
/// <c>src/tests</c>, so a new file in the same directory is not covered.
/// </remarks>
internal static class QuarantinedFixtures
{
    private const string TotalDifficultyTransition =
        "the blockchain test harness does not support a fork transition at a total difficulty (an 'AtDiff' network)";

    private const string OverWideTransactionValue =
        "the state test reader does not accept a transaction value wider than 256 bits";

    private const string RetestethBigIntPrefix = "0x:bigint ";

    /// <summary>Significant hex digits of the widest value <c>UInt256</c> holds.</summary>
    private const int UInt256HexDigits = 64;

    private static readonly QuarantinedFile[] Files =
    [
        new("LegacyTests/Cancun/BlockchainTests/TransitionTests/bcArrowGlacierToParis/difficultyFormula.json",
            TotalDifficultyTransition, ["System.FormatException", "'DiffC0000'"]),
        new("LegacyTests/Cancun/BlockchainTests/TransitionTests/bcArrowGlacierToParis/powToPosBlockRejection.json",
            TotalDifficultyTransition, ["System.FormatException", "'DiffC0000'"]),
        new("LegacyTests/Cancun/BlockchainTests/TransitionTests/bcArrowGlacierToParis/powToPosTest.json",
            TotalDifficultyTransition, ["System.FormatException", "'DiffC0000'"]),
        new("LegacyTests/Cancun/GeneralStateTests/stTransactionTest/ValueOverflow.json",
            OverWideTransactionValue, ["System.Text.Json.JsonException", "Nethermind.Int256.UInt256", "$.ValueOverflow.transaction.value["],
            OverWideValueTest: "ValueOverflow"),
        new("LegacyTests/Cancun/GeneralStateTests/stTransactionTest/ValueOverflowParis.json",
            OverWideTransactionValue, ["System.Text.Json.JsonException", "Nethermind.Int256.UInt256", "$.ValueOverflowParis.transaction.value["],
            OverWideValueTest: "ValueOverflowParis"),
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
            if (path.EndsWith('/' + file.Path, StringComparison.Ordinal) && file.FailsWith(path, error))
                Assert.Ignore($"Quarantined: {file.Reason}. File: {name}. Load error: {error}");
        }
    }

    /// <summary>
    /// Whether the transaction value of test <paramref name="testName"/> at which the reader stopped, as named by the
    /// JSON path in <paramref name="error"/>, is a hex quantity wider than 256 bits.
    /// </summary>
    /// <remarks>
    /// The <c>UInt256</c> reader fails with the same exception and path on any value it cannot read, such as a bare
    /// <c>0x</c> or a token that is not a string; only an over-wide quantity is the unsupported input. Reads the file
    /// only after its path and error matched, and answers no when the file cannot be read back.
    /// </remarks>
    private static bool StopsAtOverWideValue(string file, string testName, string error)
    {
        string valuePath = $"$.{testName}.transaction.value[";
        int start = error.IndexOf(valuePath, StringComparison.Ordinal);
        if (start < 0)
            return false;

        start += valuePath.Length;
        int end = error.IndexOf(']', start);
        if (end < 0 || !int.TryParse(error.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            return false;

        try
        {
            using JsonDocument fixture = JsonDocument.Parse(File.ReadAllBytes(file));
            return fixture.RootElement.TryGetProperty(testName, out JsonElement fixtureTest)
                && fixtureTest.TryGetProperty("transaction", out JsonElement transaction)
                && transaction.TryGetProperty("value", out JsonElement values)
                && values.ValueKind == JsonValueKind.Array
                && index < values.GetArrayLength()
                && values[index].ValueKind == JsonValueKind.String
                && IsOverWideHexQuantity(values[index].GetString()!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool IsOverWideHexQuantity(string value)
    {
        ReadOnlySpan<char> quantity = value.AsSpan();
        if (quantity.StartsWith(RetestethBigIntPrefix, StringComparison.Ordinal))
            quantity = quantity[RetestethBigIntPrefix.Length..];

        if (!quantity.StartsWith("0x", StringComparison.Ordinal))
            return false;

        ReadOnlySpan<char> digits = quantity[2..].TrimStart('0');
        return digits.Length > UInt256HexDigits && !digits.ContainsAnyExcept("0123456789abcdefABCDEF");
    }

    private sealed record QuarantinedFile(string Path, string Reason, string[] ErrorSignature, string? OverWideValueTest = null)
    {
        public bool FailsWith(string file, string error)
        {
            foreach (string part in ErrorSignature)
            {
                if (!error.Contains(part, StringComparison.Ordinal))
                    return false;
            }

            return OverWideValueTest is null || StopsAtOverWideValue(file, OverWideValueTest, error);
        }
    }
}
