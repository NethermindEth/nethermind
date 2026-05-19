// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.TxPool;

public class BlobCustodyTracker : IBlobCustodyTracker
{
    private readonly object _sync = new();
    private BlobCellMask _currentMask = BlobCellMask.Empty;

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
