// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class BlocksProcessingEventArgs : EventArgs
    {
        public IReadOnlyList<Block> Blocks { get; }

        public BlocksProcessingEventArgs(IReadOnlyList<Block> blocks)
        {
            Blocks = blocks;
        }
    }
}
