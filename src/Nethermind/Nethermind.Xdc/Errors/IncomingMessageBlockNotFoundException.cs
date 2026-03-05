// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using System;

namespace Nethermind.Xdc.Errors;

public class IncomingMessageBlockNotFoundException(Hash256 incomingBlockHash, long incomingBlockNumber, Exception? innerException = null)
    : BlockchainException($"proposed block is not found hash: {incomingBlockHash}, block number: {incomingBlockNumber}", innerException)
{
    public Hash256 IncomingBlockHash { get; } = incomingBlockHash;
    public long IncomingBlockNumber { get; } = incomingBlockNumber;
}
