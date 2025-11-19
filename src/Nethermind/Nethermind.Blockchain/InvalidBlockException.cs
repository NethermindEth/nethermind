// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public class InvalidBlockException(BlockHeader block, string message, Exception? innerException = null)
    : BlockchainException(message, innerException)
{
    public InvalidBlockException(Block block, string message, Exception? innerException = null)
        : this(block.Header, message, innerException) { }

    public BlockHeader InvalidBlock { get; } = block;
}
