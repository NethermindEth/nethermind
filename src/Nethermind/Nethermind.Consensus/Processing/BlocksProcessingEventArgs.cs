// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class BlocksProcessingEventArgs(IReadOnlyList<Block> blocks) : EventArgs
    {
        public IReadOnlyList<Block> Blocks { get; } = blocks;
    }
}
