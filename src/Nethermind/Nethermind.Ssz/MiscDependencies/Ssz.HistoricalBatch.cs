// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
