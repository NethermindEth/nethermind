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

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    class CostTracker
    {
        // These are the initial defaults from geth.
        // It probably doesn't make sense to define them here in the finished implementation, since the client will want to use the values supplied by the server

        // TODO: Benchmark finished implementation and update based on our actual serve times.
        // TODO: Implement cost scaling to account for users with different capabilities - https://github.com/ethereum/go-ethereum/blob/01d92531ee0993c0e6e5efe877a1242bfd808626/les/costtracker.go#L437
        // TODO: Implement multiple cost lists, so it can be limited based on the minimum of available bandwidth, cpu time, etc. - https://github.com/ethereum/go-ethereum/blob/01d92531ee0993c0e6e5efe877a1242bfd808626/les/costtracker.go#L186
        // TODO: This might be better as a dictionary
        public static RequestCostItem[] DefaultRequestCostTable = new RequestCostItem[]
        {
            new RequestCostItem(LesMessageCode.GetBlockHeaders, 150_000, 30_000),
            new RequestCostItem(LesMessageCode.GetBlockBodies, 0, 700_000),
            new RequestCostItem(LesMessageCode.GetReceipts, 0, 1_000_000),
            new RequestCostItem(LesMessageCode.GetContractCodes, 0, 450_000),
            new RequestCostItem(LesMessageCode.GetProofsV2, 0, 600_000),
            new RequestCostItem(LesMessageCode.GetHelperTrieProofs, 0, 1_000_000),
            new RequestCostItem(LesMessageCode.SendTxV2, 0, 450_000),
            new RequestCostItem(LesMessageCode.GetTxStatus, 0, 250_000)
        };

    }

}
