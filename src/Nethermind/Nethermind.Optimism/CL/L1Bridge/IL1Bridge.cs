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
    ChannelReader<L1Block> NewHeadReader { get; }
    Task<BlobSidecar[]> GetBlobSidecars(ulong slotNumber, int indexFrom, int indexTo);
    Task<L1Block> GetBlock(ulong blockNumber);
    Task<L1Block> GetBlockByHash(Hash256 blockHash);
    Task<ReceiptForRpc[]> GetReceiptsByBlockHash(Hash256 blockHash);
    // For testing purposes. Will trigger L1 traversal
    void SetCurrentL1Head(ulong blockNumber, Hash256 blockHash);
    void Start();
}
