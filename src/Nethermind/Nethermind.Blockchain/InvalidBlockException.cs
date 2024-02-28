// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public class InvalidBlockException : BlockchainException
{
    public InvalidBlockException(Block block, string message, Exception? innerException = null)
        : base($"Invalid block: {block} : {message}", innerException) => InvalidBlock = block.Header;

    public InvalidBlockException(BlockHeader block, string message, Exception? innerException = null)
        : base($"Invalid block: {block} : {message}", innerException) => InvalidBlock = block;

    public BlockHeader InvalidBlock { get; }
}
