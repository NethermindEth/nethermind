// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;

namespace Nethermind.Consensus.Tracing;

public class GethStyleTracerRequest
{
    public Block Block { get; }
    public Hash256? TxHash { get; }
    public GethTraceOptions Options { get; }
    public ProcessingOptions ProcessingOptions { get; }

    public GethStyleTracerRequest(
        Block block,
        Hash256? txHash,
        GethTraceOptions options,
        ProcessingOptions processingOptions = ProcessingOptions.Trace)
    {
        Block = block;
        TxHash = txHash;
        Options = options;
        ProcessingOptions = processingOptions;
    }
} 