// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db.Blooms;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Api
{
    public interface IApiWithStores : IBasicApi
    {
        IBlobTxStorage BlobTxStorage { get; }
        IBlockTree BlockTree { get; }
        IBloomStorage? BloomStorage { get; }
        IChainLevelInfoRepository? ChainLevelInfoRepository { get; }
        ISigner? EngineSigner { get; set; }
        ISignerStore? EngineSignerStore { get; set; }
        [SkipServiceCollection]
        IProtectedPrivateKey? NodeKey { get; set; }
        IReceiptStorage? ReceiptStorage { get; }
        IReceiptFinder ReceiptFinder { get; }
        IWallet? Wallet { get; set; }
    }
}
