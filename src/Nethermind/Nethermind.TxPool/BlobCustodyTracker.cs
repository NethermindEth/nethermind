// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.TxPool;

public class BlobCustodyTracker : IBlobCustodyTracker
{
    private readonly object _sync = new();
    // Custody defaults to all cells (supernode behavior) until the consensus client provides
    // the node's actual custody via engine_forkchoiceUpdatedV4, matching geth. An empty default
    // would leave the node sampling almost nothing and unable to serve engine_getBlobsV4.
    private BlobCellMask _currentMask = BlobCellMask.Full;

    public BlobCellMask CurrentMask
    {
        get
        {
            lock (_sync)
            {
                return _currentMask;
            }
        }
    }

    public event EventHandler<BlobCellMask>? CustodyChanged;

    public bool Update(BlobCellMask mask)
    {
        lock (_sync)
        {
            if (_currentMask == mask)
            {
                return false;
            }

            _currentMask = mask;
        }

        CustodyChanged?.Invoke(this, mask);
        return true;
    }
}
