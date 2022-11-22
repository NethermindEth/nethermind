// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            new(LesMessageCode.GetBlockHeaders, 150_000, 30_000),
            new(LesMessageCode.GetBlockBodies, 0, 700_000),
            new(LesMessageCode.GetReceipts, 0, 1_000_000),
            new(LesMessageCode.GetContractCodes, 0, 450_000),
            new(LesMessageCode.GetProofsV2, 0, 600_000),
            new(LesMessageCode.GetHelperTrieProofs, 0, 1_000_000),
            new(LesMessageCode.SendTxV2, 0, 450_000),
            new(LesMessageCode.GetTxStatus, 0, 250_000)
        };

    }

}
