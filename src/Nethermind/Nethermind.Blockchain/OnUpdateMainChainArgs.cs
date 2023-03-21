// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public class OnUpdateMainChainArgs: EventArgs
{
    public IReadOnlyList<Block> Blocks { get; }

    public bool WereProcessed { get; }

    public OnUpdateMainChainArgs(IReadOnlyList<Block> blocks, bool wereProcessed)
    {
        Blocks = blocks;
        WereProcessed = wereProcessed;
    }
}
