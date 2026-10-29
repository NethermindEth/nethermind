// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp.Eip7928;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Finalises the block by stamping the generated BAL + its encoded RLP + its hash onto the
/// produced block.
/// </summary>
public partial class BlockAccessListManager
{
    private static readonly StringLabel PrepareLabel = new("prepare");
    private static readonly StringLabel GasValidationLabel = new("gas_validation");
    private static readonly StringLabel SealLabel = new("seal");
    private static readonly StringLabel ValidateLogsLabel = new("validate_logs");
    private static readonly StringLabel MergeLabel = new("merge");
    private static readonly StringLabel ValidateMergedLabel = new("validate_merged");
    private static readonly StringLabel EncodeLabel = new("encode");

    /// <summary>Observes the time since <paramref name="start"/> as <paramref name="part"/>.</summary>
    /// <returns>The current timestamp, to start timing the next part.</returns>
    private static long ObserveTime(long start, StringLabel part)
    {
        long now = Stopwatch.GetTimestamp();
        Metrics.BlockAccessListProcessingTimeMicros.Observe(Stopwatch.GetElapsedTime(start, now).TotalMicroseconds, part);
        return now;
    }

    public void SetBlockAccessList(Block block)
    {
        if (!_blockAccessListsEnabled)
        {
            return;
        }

        if (block.IsGenesis)
        {
            block.Header.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
            return;
        }

        CheckInitialized();
        long start = Stopwatch.GetTimestamp();
        ReadOnlySpan<BlockAccessListLog> logs = _txProcessorWithWorldStateManager.SealLogs();
        start = ObserveTime(start, SealLabel);
        if (VerifyOnly)
        {
            ValidateLogs(block);
            ObserveTime(start, ValidateLogsLabel);
            return;
        }

        GeneratedBlockAccessList generated = GeneratedBlockAccessList.Merge(logs);
        start = ObserveTime(start, MergeLabel);
        // Set before validating, so a block rejected for its access list keeps the generated one.
        GeneratedBlockAccessList = generated;
        block.GeneratedBlockAccessList = generated;

        if (_validateBlockAccessList)
        {
            ValidateMergedBlockAccessList(block, generated);
            start = ObserveTime(start, ValidateMergedLabel);

            // A match encodes to the suggested canonical wire bytes, as the decoder accepts nothing else, so their hash
            // stands in for the re-encode.
            if (block.BlockAccessList!.WireHash is { } wireHash)
            {
                block.Header.BlockAccessListHash = wireHash;
                return;
            }
        }

        block.EncodedBlockAccessList = BlockAccessListDecoder.EncodeToBytes(generated);
        block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
        ObserveTime(start, EncodeLabel);
    }
}
