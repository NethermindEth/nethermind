// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

public class BlockRemovedEventArgs : BlockHashEventArgs
{
    public string? Message { get; }

    public BlockRemovedEventArgs(Hash256 blockHash, ProcessingResult processingResult, string? message = null) : base(blockHash, processingResult)
    {
        Message = message;
    }

    public BlockRemovedEventArgs(Hash256 blockHash, ProcessingResult processingResult, Exception exception) : base(blockHash, processingResult, exception)
    {
    }
}
