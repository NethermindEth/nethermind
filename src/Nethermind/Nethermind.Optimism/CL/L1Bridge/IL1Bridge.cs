// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.L1Bridge;

public interface IL1Bridge
{
    Channel<(BeaconBlock, ReceiptForRpc[])> NewHeadChannel { get; }
    // TODO: use indices to skip blobs
    Task<BlobSidecar[]?> GetBlobSidecars(ulong slotNumber);
    Task<BlockForRpc?> GetBlock(ulong blockNumber);
    Task<BlockForRpc?> GetBlockByHash(Hash256 blockHash);
    Task<ReceiptForRpc[]?> GetReceiptsByBlockHash(Hash256 blockHash);
    // For testing purposes. Will trigger L1 traversal
    void SetCurrentL1Head(ulong slotNumber);
    void Start();
}
