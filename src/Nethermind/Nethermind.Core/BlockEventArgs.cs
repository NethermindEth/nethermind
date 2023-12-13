// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class BlockEventArgs : EventArgs
    {
        public Block Block { get; }

        public BlockEventArgs(Block block)
        {
            Block = block;
        }
    }
}
