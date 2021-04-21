//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.Int256;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade;
using Nethermind.Logging;
using Nethermind.Blockchain;
using Nethermind.State;

namespace Nethermind.Mev
{
    public partial class MevRpcModule : IMevRpcModule
    {
        // from constructor arguments
        private readonly IMevConfig _mevConfig;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly MevPlugin _mevPlugin;

        // from mevplugin nethermind api
        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainBridge _blockchainBridge; 
        private readonly IStateReader _stateReader;

        public MevRpcModule(IMevConfig mevConfig, IJsonRpcConfig jsonRpcConfig, MevPlugin mevPlugin)
        {
            _mevConfig = mevConfig;
            _jsonRpcConfig = jsonRpcConfig;
            _mevPlugin = mevPlugin;

            _logger = mevPlugin.NethermindApi.LogManager.GetClassLogger();
            _blockTree = mevPlugin.NethermindApi.BlockTree ?? throw new NullReferenceException("BlockTree");
            _blockchainBridge = mevPlugin.NethermindApi.CreateBlockchainBridge();
            _stateReader = mevPlugin.NethermindApi.StateReader ?? throw new NullReferenceException("StateReader");
        }

        public ResultWrapper<bool> eth_sendBundle(TransactionForRpc[] transactions, UInt256 blockNumber, UInt256 minTimestamp, UInt256 maxTimestamp)
        {
            ulong chainId = _blockchainBridge.GetChainId();
            var transactions_ = transactions.Select(tx => tx.ToTransaction(chainId)).ToList<Transaction>(); 

            BigInteger blockNumber_;
            blockNumber.Convert(out blockNumber_);
            BigInteger minTimestamp_;
            minTimestamp.Convert(out minTimestamp_);
            BigInteger maxTimestamp_;
            maxTimestamp.Convert(out maxTimestamp_);

            MevBundleForRpc bundle = new MevBundleForRpc(transactions_, blockNumber_, minTimestamp_, maxTimestamp_);
            
            _mevPlugin.AddMevBundle(bundle);
            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<FeeToFrequency> mev_feeDistribution()
        {
            throw new NotImplementedException();
        }
            
        public ResultWrapper<TxToResult> eth_callBundle(TransactionForRpc[] transactionCalls, BlockParameter blockParameter, UInt256? blockTimestamp) => throw new NotImplementedException();
            // new CallBundleTxExecutor(_blockchainBridge, _blockTree, _jsonRpcConfig).ExecuteBundleTx(transactionCalls, blockParameter, blockTimestamp);
    }

    // TODO move and write serializers, eg. syncingResultConverter
    public class TxToResult
    {
        public List<(Keccak, string)> pairs { get; set; } = new List<(Keccak, string)>();
    }

    public class FeeToFrequency
    {

    }

}
