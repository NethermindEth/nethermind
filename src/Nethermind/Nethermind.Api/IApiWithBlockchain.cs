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
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Api
{
    public interface IApiWithBlockchain : IApiWithStores, IBlockchainBridgeFactory
    {
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForInit => (this, this);
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForBlockchain => (this, this);
        (IApiWithBlockchain GetFromApi, IApiWithBlockchain SetInApi) ForProducer => (this, this);
        
        IBlockchainProcessor? BlockchainProcessor { get; set; }
        CompositeBlockPreprocessorStep BlockPreprocessor { get; }
        // IBlockPreprocessorStep RecoveryStep => BlockPreprocessor;
        IBlockProcessingQueue? BlockProcessingQueue { get; set; }
        IBlockProcessor? MainBlockProcessor { get; set; }
        IBlockProducer? BlockProducer { get; set; }
        IBlockValidator? BlockValidator { get; set; }
        IEnode? Enode { get; set; }
        IFilterStore FilterStore { get; set; }
        IFilterManager FilterManager { get; set; }
        IHeaderValidator? HeaderValidator { get; set; }
        IRewardCalculatorSource? RewardCalculatorSource { get; set; }
        ISealer? Sealer { get; set; }
        ISealValidator? SealValidator { get; set; }
        IStateProvider? StateProvider { get; set; }
        IReadOnlyStateProvider? ChainHeadStateProvider { get; set; }
        IStateReader? StateReader { get; set; }
        IStorageProvider? StorageProvider { get; set; }
        ITransactionProcessor? TransactionProcessor { get; set; }
        ITxSender? TxSender { get; set; }
        ITxPool? TxPool { get; set; }
        ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
    }
}
