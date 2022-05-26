//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Synchronization;

public class MovablePivot : IPivot
{
    private readonly IPivot _fallbackPivot;
    private BlockHeader? _pivotHeader;

    public MovablePivot(IPivot fallbackPivot)
    {
        _fallbackPivot = fallbackPivot;
    }

    public BlockHeader? PivotHeader
    {
        get => _pivotHeader;
        set
        {
            if (_pivotHeader != value)
            {
                _pivotHeader = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public long PivotNumber => PivotHeader?.Number ?? _fallbackPivot.PivotNumber;

    public Keccak? PivotHash => PivotHeader is not null ? PivotHeader.Hash : _fallbackPivot.PivotHash;

    public UInt256? PivotTotalDifficulty => PivotHeader is not null ? PivotHeader.TotalDifficulty : _fallbackPivot.PivotTotalDifficulty;

    public long PivotDestinationNumber => _fallbackPivot.PivotNumber;

    public event EventHandler? Changed;
}
