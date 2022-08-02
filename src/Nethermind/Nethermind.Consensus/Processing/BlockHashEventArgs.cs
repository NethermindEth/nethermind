//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

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
