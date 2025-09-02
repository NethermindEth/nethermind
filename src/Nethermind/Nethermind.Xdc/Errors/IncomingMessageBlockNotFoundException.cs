// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc.Errors;

public class IncomingMessageBlockNotFoundException : Exception
{
    public string Type { get; }
    public string IncomingBlockHash { get; } // using string.Hex in Go
    public long IncomingBlockNumber { get; }
    public Exception InnerErr { get; }

    public IncomingMessageBlockNotFoundException(string type, string incomingBlockHash, long incomingBlockNumber, Exception innerErr)
        :base($"{type} proposed block is not found hash: {incomingBlockHash}, block number: {incomingBlockNumber}, error: {innerErr?.Message}")
    {
        Type = type;
        IncomingBlockHash = incomingBlockHash;
        IncomingBlockNumber = incomingBlockNumber;
        InnerErr = innerErr;
    }

}
