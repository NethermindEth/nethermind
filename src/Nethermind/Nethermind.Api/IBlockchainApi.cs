//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

#nullable enable
using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Crypto;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Api
{
    public interface IApiWithStores : IBasicApi
    {
        IBlockTree? BlockTree { get; set; }
        IBloomStorage? BloomStorage { get; set; }
        IChainLevelInfoRepository? ChainLevelInfoRepository { get; set; }
        ISigner? EngineSigner { get; set; }
        ISignerStore? EngineSignerStore { get; set; }
    }
    
    public interface IApiWithBlockchain : IApiWithStores, IBlockchainBridgeFactory
    {
        IBlockchainProcessor? BlockchainProcessor { get; set; }
        CompositeBlockPreprocessorStep BlockPreprocessor { get; }
        // IBlockPreprocessorStep RecoveryStep => BlockPreprocessor;
        IBlockProcessingQueue? BlockProcessingQueue { get; set; }
        IBlockProcessor? MainBlockProcessor { get; set; }
        IBlockProducer? BlockProducer { get; set; }
        IBlockValidator? BlockValidator { get; set; }
        IEnode? Enode { get; set; }
        IFileSystem FileSystem { get; set; }
        IFilterStore FilterStore { get; set; }
        IFilterManager FilterManager { get; set; }
        IHeaderValidator? HeaderValidator { get; set; }
        ILogFinder LogFinder { get; set; }
        IMessageSerializationService MessageSerializationService { get; }
        IMonitoringService MonitoringService { get; set; }
        IReceiptStorage? ReceiptStorage { get; set; }
        IReceiptFinder? ReceiptFinder { get; set; }
        IRewardCalculatorSource? RewardCalculatorSource { get; set; }
        IRpcModuleProvider RpcModuleProvider { get; set; }
        ISealer? Sealer { get; set; }
        ISealValidator? SealValidator { get; set; }
        ISyncModeSelector? SyncModeSelector { get; set; }
        ISyncPeerPool? SyncPeerPool { get; set; }
        ISynchronizer? Synchronizer { get; set; }
        ISyncServer? SyncServer { get; set; }
        IStateProvider? StateProvider { get; set; }
        IStateReader? StateReader { get; set; }
        IStorageProvider? StorageProvider { get; set; }
        ISessionMonitor? SessionMonitor { get; set; }
        IStaticNodesManager? StaticNodesManager { get; set; }
        ITransactionProcessor? TransactionProcessor { get; set; }
        ITxSender? TxSender { get; set; }
        ITxPool? TxPool { get; set; }
        ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        IWallet? Wallet { get; set; }

        ProtectedPrivateKey? NodeKey { get; set; }
    }
}
