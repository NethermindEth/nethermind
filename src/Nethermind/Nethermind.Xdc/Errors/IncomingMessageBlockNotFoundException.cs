// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc.Errors;

public class IncomingMessageBlockNotFoundException(string incomingBlockHash, long incomingBlockNumber, Exception? innerException = null)
    : Exception($"proposed block is not found hash: {incomingBlockHash}, block number: {incomingBlockNumber}", innerException)
{
    public string IncomingBlockHash { get; } = incomingBlockHash;
    public long IncomingBlockNumber { get; } = incomingBlockNumber;
}
