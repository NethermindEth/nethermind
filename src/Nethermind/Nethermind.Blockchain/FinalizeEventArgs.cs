// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public class FinalizeEventArgs(BlockHeader finalizedBlock) : EventArgs
    {
        public BlockHeader FinalizedBlock { get; } = finalizedBlock;
    }
}
