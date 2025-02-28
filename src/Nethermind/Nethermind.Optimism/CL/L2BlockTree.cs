// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Optimism.CL;

public class L2BlockTree : IL2BlockTree
{
    private const int L2BlockTreeCapacity = 1024;

    private int _start = 0;
    private int _count = 0;
    private readonly L2Block[] _blocks = new L2Block[L2BlockTreeCapacity];

    public ulong? HeadBlockNumber => GetHighestBlock()?.Number;

    public L2Block? GetBlockByNumber(ulong number)
    {
        if (_count == 0 || number < _blocks[_start].Number)
        {
            return null;
        }
        ulong index = number - _blocks[_start].Number;
        if ((int)index >= _count)
        {
            return null;
        }
        return _blocks[((int)index + _start) % L2BlockTreeCapacity];
    }

    public bool TryAddBlock(L2Block block)
    {
        if (_count == 0)
        {
            _blocks[0] = block;
            _count = 1;
            return true;
        }

        L2Block parent = GetHighestBlock()!;
        if (parent.Number + 1 != block.Number || parent.Hash != block.ParentHash)
        {
            return false;
        }

        if (_count == L2BlockTreeCapacity)
        {
            _blocks[_start] = block;
            _start = (_start + 1) % L2BlockTreeCapacity;
        }
        else
        {
            _blocks[_count] = block;
            _count++;
        }
        return true;
    }

    public L2Block? GetHighestBlock()
    {
        if (_count == 0)
        {
            return null;
        }

        if (_count == L2BlockTreeCapacity)
        {
            return _blocks[(_start + L2BlockTreeCapacity - 1) % L2BlockTreeCapacity];
        }
        return _blocks[_count - 1];
    }
}
