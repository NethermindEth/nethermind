// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Optimism.CL;

public class L2BlockTree : IL2BlockTree
{
    // TODO: pruning
    private readonly List<L2Block> _blocks = new();

    public L2Block? GetBlockByNumber(ulong number)
    {
        if (_blocks.Count == 0 || number < _blocks[0].Number)
        {
            return null;
        }
        ulong index = number - _blocks[0].Number;
        if ((int)index >= _blocks.Count)
        {
            return null;
        }
        return _blocks[(int)index];
    }

    public bool TryAddBlock(L2Block block)
    {
        if (_blocks.Count == 0)
        {
            _blocks.Add(block);
            return true;
        }

        L2Block parent = _blocks[_blocks.Count - 1];
        if (parent.Number + 1 != block.Number || parent.Hash != block.ParentHash)
        {
            return false;
        }

        _blocks.Add(block);
        return true;
    }

    public L2Block? GetHighestBlock()
    {
        if (_blocks.Count == 0)
        {
            return null;
        }
        return _blocks[_blocks.Count - 1];
    }
}
