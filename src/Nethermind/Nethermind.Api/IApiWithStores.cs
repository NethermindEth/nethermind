// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db.Blooms;
using Nethermind.Facade.Find;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Api
{
    public interface IApiWithStores : IBasicApi
    {
        IBlobTxStorage? BlobTxStorage { get; set; }
        IBlockTree? BlockTree { get; set; }
        IBloomStorage? BloomStorage { get; set; }
        IChainLevelInfoRepository? ChainLevelInfoRepository { get; set; }
        ILogFinder? LogFinder { get; set; }
        ISigner? EngineSigner { get; set; }
        ISignerStore? EngineSignerStore { get; set; }
        [SkipServiceCollection]
        IProtectedPrivateKey? NodeKey { get; set; }
        IReceiptStorage? ReceiptStorage { get; set; }
        IReceiptFinder? ReceiptFinder { get; set; }
        IReceiptMonitor? ReceiptMonitor { get; set; }
        IWallet? Wallet { get; set; }
        IBadBlockStore? BadBlocksStore { get; set; }
    }
}
