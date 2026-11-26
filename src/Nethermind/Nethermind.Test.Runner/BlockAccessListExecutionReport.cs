// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using Ethereum.Test.Base;
using Nethermind.Core;

namespace Nethermind.Test.Runner;

/// <summary>
/// Writes one JSON line per executed block naming the executor that ran it, and one more when a block the
/// parallel executor rejected is re-run sequentially. Lines from fixtures run in parallel interleave, so
/// each carries the block hash rather than relying on its position.
/// </summary>
public sealed class BlockAccessListExecutionReport(TextWriter output) : IBlockAccessListExecutionReport
{
    public void OnExecutionPathChosen(Block block, string? sequentialReason) =>
        Write(new
        {
            @event = "balExecution",
            block = block.Number,
            hash = block.Hash?.ToString(),
            path = sequentialReason is null ? "parallel" : "sequential",
            reason = sequentialReason ?? ""
        });

    public void OnSequentialRetry(Block block, Exception parallelError, Exception? sequentialError) =>
        Write(new
        {
            @event = "balFallback",
            block = block.Number,
            hash = block.Hash?.ToString(),
            parallelError = parallelError.Message,
            sequentialResult = sequentialError is null ? "valid" : "invalid",
            sequentialError = sequentialError?.Message ?? ""
        });

    private void Write<T>(T line) => output.WriteLine(JsonSerializer.Serialize(line));
}
