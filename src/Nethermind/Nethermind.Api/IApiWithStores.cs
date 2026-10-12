// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Api
{
    public interface IApiWithStores : IBasicApi
    {
        IBlobTxStorage BlobTxStorage { get; }
        IBlockTree BlockTree { get; }
        ISigner EngineSigner { get; }
        IReceiptFinder ReceiptFinder { get; }
        IWallet Wallet { get; }
    }
}
