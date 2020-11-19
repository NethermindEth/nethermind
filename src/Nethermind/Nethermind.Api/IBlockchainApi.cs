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
using System.Collections.Generic;
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
using Nethermind.Consensus.Transactions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.PubSub;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.WebSockets;

namespace Nethermind.Api
{
    public interface IBlockchainApi : IBlockchainBridgeFactory
    {
        IBlockchainProcessor? BlockchainProcessor { get; set; }
        IBlockDataRecoveryStep? RecoveryStep { get; set; }
        IBlockProcessingQueue? BlockProcessingQueue { get; set; }
        IBlockProcessor? MainBlockProcessor { get; set; }
        IBlockProducer? BlockProducer { get; set; }
        IBlockTree? BlockTree { get; set; }
        IBlockValidator? BlockValidator { get; set; }
        IBloomStorage? BloomStorage { get; set; }
        IChainLevelInfoRepository? ChainLevelInfoRepository { get; set; }
        IDbProvider? DbProvider { get; set; }
        IEnode? Enode { get; set; }
        IFileSystem FileSystem { get; set; }
        IFilterStore FilterStore { get; set; }
        IFilterManager FilterManager { get; set; }
        IHeaderValidator? HeaderValidator { get; set; }
        IKeyStore? KeyStore { get; set; }
        ILogFinder LogFinder { get; set; }
        IMessageSerializationService MessageSerializationService { get; }
        IMonitoringService MonitoringService { get; set; }
        IReceiptStorage? ReceiptStorage { get; set; }
        IReceiptFinder? ReceiptFinder { get; set; }
        IRewardCalculatorSource? RewardCalculatorSource { get; set; }
        IRpcModuleProvider RpcModuleProvider { get; set; }
        ISealer? Sealer { get; set; }
        ISealValidator? SealValidator { get; set; }
        ISigner? EngineSigner { get; set; }
        ISignerStore? EngineSignerStore { get; set; }
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
        ProtectedPrivateKey? OriginalSignerKey { get; set; }
    }
}
