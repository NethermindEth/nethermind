// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public class InvalidInclusionListException : BlockchainException
{
    public InvalidInclusionListException(Block block, string message, Exception? innerException = null)
        : base(message, innerException) => Block = block.Header;

    public InvalidInclusionListException(BlockHeader block, string message, Exception? innerException = null)
        : base(message, innerException) => Block = block;

    public BlockHeader Block { get; }
}
