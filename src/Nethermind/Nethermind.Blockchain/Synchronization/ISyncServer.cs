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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISyncServer
    {
        void HintBlock(Keccak hash, long number, Node receivedFrom);
        void AddNewBlock(Block block, Node node);
        TxReceipt[][] GetReceipts(Keccak[] blockHashes);
        Block Find(Keccak hash);
        Keccak FindHash(long number);
        BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse);
        byte[][] GetNodeData(Keccak[] keys);
        int GetPeerCount();
        int ChainId { get; }
        BlockHeader Genesis { get; }
        BlockHeader Head { get; }
    }
}