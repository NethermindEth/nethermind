// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

public class BlockHashEventArgs : EventArgs
{
    public Keccak BlockHash { get; }
    public ProcessingResult ProcessingResult { get; }
    public Exception? Exception { get; }

    public BlockHashEventArgs(Keccak blockHash, ProcessingResult processingResult, Exception? exception = null)
    {
        BlockHash = blockHash;
        ProcessingResult = processingResult;
        Exception = exception;
    }
}

public enum ProcessingResult
{
    /// <summary>
    /// Processing was successful
    /// </summary>
    Success,

    /// <summary>
    /// Queue exception on adding block
    /// </summary>
    QueueException,

    /// <summary>
    /// Block hash wasn't found
    /// </summary>
    MissingBlock,

    /// <summary>
    /// Exception during processing
    /// </summary>
    Exception,

    /// <summary>
    /// Processing failed
    /// </summary>
    ProcessingError
}
