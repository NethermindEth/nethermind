//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Linq;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public static int HistoricalBatchLength()
        {
            return 2 * SlotsPerHistoricalRoot * Ssz.RootLength;
        }

        public static void Encode(Span<byte> span, HistoricalBatch container)
        {
            if (span.Length != Ssz.HistoricalBatchLength())
            {
                ThrowTargetLength<HistoricalBatch>(span.Length, Ssz.HistoricalBatchLength());
            }

            Encode(span.Slice(0, Ssz.HistoricalBatchLength() / 2), container.BlockRoots);
            Encode(span.Slice(Ssz.HistoricalBatchLength() / 2), container.StateRoots);
        }

        public static HistoricalBatch? DecodeHistoricalBatch(Span<byte> span)
        {
            if (span.Length != Ssz.HistoricalBatchLength()) ThrowSourceLength<HistoricalBatch>(span.Length, Ssz.HistoricalBatchLength());

            Root[] blockRoots = DecodeRoots(span.Slice(0, Ssz.HistoricalBatchLength() / 2));
            Root[] stateRoots = DecodeRoots(span.Slice(Ssz.HistoricalBatchLength() / 2));
            HistoricalBatch container = new HistoricalBatch(blockRoots, stateRoots);
            return container;
        }
    }
}