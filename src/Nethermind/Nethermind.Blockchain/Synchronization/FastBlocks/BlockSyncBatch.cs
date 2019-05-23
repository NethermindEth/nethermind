/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class BlockSyncBatch
    {
        public bool Prioritized { get; set; }
        
        public HeadersSyncBatch HeadersSyncBatch { get; set; }
        public BodiesSyncBatch BodiesSyncBatch { get; set; }
        public SyncPeerAllocation AssignedPeer { get; set; }
        
        public DateTime CreationTimeUtc { get; set; } = DateTime.UtcNow;

        public UInt256? MinTotalDifficulty { get; set; }

        public long? MinNumber { get; set; }

        public override string ToString()
        {
            string bodiesOrHeaders = HeadersSyncBatch != null ? "HEADERS" : "BODIES";
            string startBlock = HeadersSyncBatch?.StartNumber.ToString() ?? HeadersSyncBatch?.StartHash.ToString();
            string endBlock = (HeadersSyncBatch?.StartNumber != null ? HeadersSyncBatch.StartNumber + (HeadersSyncBatch.Reverse ? -1 : 1) * (HeadersSyncBatch.RequestSize - 1) : HeadersSyncBatch?.RequestSize - 1).ToString();
            string age = $"{(DateTime.UtcNow - CreationTimeUtc).TotalMilliseconds:F0}ms";
            return $"{bodiesOrHeaders} [{startBlock}, {endBlock}] age: {age}, reverse: {HeadersSyncBatch?.Reverse}, size: {HeadersSyncBatch?.RequestSize ?? 0}, skip: {HeadersSyncBatch?.Skip}, min#: {MinNumber}, min diff: {MinTotalDifficulty}";
        }
    }
}