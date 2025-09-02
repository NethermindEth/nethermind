// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc.Errors;

public class ErrIncomingMessageBlockNotFound : Exception
{
    public string Type { get; }
    public string IncomingBlockHash { get; } // using string.Hex in Go
    public long IncomingBlockNumber { get; }
    public Exception InnerErr { get; }

    public ErrIncomingMessageBlockNotFound(string type, string incomingBlockHash, long incomingBlockNumber, Exception innerErr)
    {
        Type = type;
        IncomingBlockHash = incomingBlockHash;
        IncomingBlockNumber = incomingBlockNumber;
        InnerErr = innerErr;
    }

    public override string Message =>
        $"{Type} proposed block is not found hash: {IncomingBlockHash}, block number: {IncomingBlockNumber}, error: {InnerErr?.Message}";
}
