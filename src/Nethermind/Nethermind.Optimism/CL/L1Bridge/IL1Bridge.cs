// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.L1Bridge;

public interface IL1Bridge
{
    event Action<BeaconBlock, ReceiptForRpc[]>? OnNewL1Head;
    // TODO: use indices to skip blobs
    Task<BlobSidecar[]?> GetBlobSidecars(ulong slotNumber);
    Task<BlockForRpc?> GetBlock(ulong blockNumber);
    Task<BlockForRpc?> GetBlockByHash(Hash256 blockHash);
    Task<ReceiptForRpc[]?> GetReceiptsByBlockHash(Hash256 blockHash);
    void Start();
}
