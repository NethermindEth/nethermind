// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class BlockEventArgs(Block block) : EventArgs
    {
        public Block Block { get; } = block;

        /// <summary>Indicates that block processing should not run inline on the suggestion caller.</summary>
        public bool NonBlockingProcessing { get; init; }
    }
}
