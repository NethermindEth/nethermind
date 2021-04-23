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
using System.Text;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.Int256;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade;
using Nethermind.Logging;
using Nethermind.Blockchain;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Newtonsoft.Json;

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

        public ResultWrapper<bool> eth_sendBundle(byte[][] transactions, UInt256 blockNumber, UInt256 minTimestamp, UInt256 maxTimestamp)
        {
            Transaction[] txs = Decode(transactions);
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

        private Transaction[] Decode(byte[][] transactions)
        {
            Rlp.Decode<Transaction>()
        }

        public ResultWrapper<TxsToResults> eth_callBundle(byte[][] transactions, BlockParameter blockParameter, UInt256? timestamp)
        {
            
        }

        public ResultWrapper<FeeToFrequency> neth_feeDistribution()
        {
            // decentralize mev_relay first?
            // integration with ndm
            // eth_subscribe
            throw new NotImplementedException();
        }
            
        public ResultWrapper<TxsToResults> eth_callBundle2(TransactionForRpc[] transactions, BlockParameter blockParameter, UInt256? blockTimestamp) 
        {
            // WRONG
            if (_mevPlugin.NethermindApi.MainBlockProcessor == null)
                return ResultWrapper<TxsToResults>.Fail("No block processor for eth_callBundle");
                
            return new CallBundleTxExecutor(_blockchainBridge, _blockTree, _jsonRpcConfig, _logger, _mevPlugin.NethermindApi.MainBlockProcessor! /*NOOO!*/)
                .ExecuteBundleTx(transactions, blockParameter, blockTimestamp);
        }
            
    }

    public class TxsToResults
    {
        public List<(Keccak, byte[])> Pairs { get; set; }
        public TxsToResults(List<(Keccak, byte[])> pairs)
        {
            Pairs = pairs;
        }
    }

    // public class TxToResultConverter : JsonConverter<TxsToResults>
    // {
    //     public override void WriteJson(JsonWriter writer, TxsToResults value, JsonSerializer serializer)
    //     {
    //         writer.WriteStartObject();

    //         foreach(var (txHash, output) in value.Pairs)
    //         {
    //             writer.WriteProperty($"{txHash.ToString()}", output, serializer);    
    //         }
                        
    //         writer.WriteEndObject();
    //     }

    //     public override TxsToResults ReadJson(JsonReader reader, Type objectType, TxsToResults existingValue, bool hasExistingValue, JsonSerializer serializer)
    //     {
    //         throw new NotSupportedException();
    //     }
    // }

    public class FeeToFrequency
    {

    }

}
