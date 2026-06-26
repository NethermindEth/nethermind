// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;

namespace Nethermind.Trie;

/// <summary>
/// Provides bounded trie write-batch sizes for non-durable bulk sync writes.
/// </summary>
public static class TrieWriteBatchSettings
{
    /// <summary>
    /// Environment variable that overrides the default DisableWAL trie write-batch size.
    /// </summary>
    public const string DisableWalBatchSizeEnvironmentVariable = "NETHERMIND_TRIE_DISABLE_WAL_BATCH_SIZE";

    /// <summary>
    /// Default number of trie-node writes per DisableWAL batch.
    /// </summary>
    public const int DefaultDisableWalBatchSize = 1024;

    /// <summary>
    /// Maximum accepted number of trie-node writes per DisableWAL batch.
    /// </summary>
    public const int MaxDisableWalBatchSize = 65_536;

    /// <summary>
    /// Gets the configured DisableWAL trie write-batch size.
    /// </summary>
    /// <param name="configuredBatchSize">Explicit batch size, or zero to read the environment variable.</param>
    /// <returns>A positive batch size clamped to <see cref="MaxDisableWalBatchSize"/>.</returns>
    public static int GetDisableWalBatchSize(int configuredBatchSize = 0)
    {
        if (configuredBatchSize > 0)
        {
            return Math.Min(configuredBatchSize, MaxDisableWalBatchSize);
        }

        string? value = Environment.GetEnvironmentVariable(DisableWalBatchSizeEnvironmentVariable);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? Math.Min(parsed, MaxDisableWalBatchSize)
            : DefaultDisableWalBatchSize;
    }
}
