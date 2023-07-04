// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public class InvalidBlockException : BlockchainException
{
    public InvalidBlockException(Block block, Exception? innerException = null)
        : base($"Invalid block: {block}", innerException) => InvalidBlock = block;

    public Block InvalidBlock { get; }
}
