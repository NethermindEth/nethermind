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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Facade;
using Nethermind.Baseline;
using Nethermind.Abi;
using Nethermind.Db;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Filters;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.Baseline
{
    public class BaselineModule : IBaselineModule
    {
        private readonly IAbiEncoder _abiEncoder;
        private readonly ILogger _logger;
        private readonly ITxPoolBridge _txPoolBridge;
        public FilterLog filterLog;

        MemDb _memdb = new MemDb();
        public BaselineModule(ITxPoolBridge txPoolBridge, IAbiEncoder abiEncoder, ILogManager logManager)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txPoolBridge = txPoolBridge ?? throw new ArgumentNullException(nameof(txPoolBridge));
        }
        public ResultWrapper<Keccak> baseline_insertLeaf(Address address, Address contractAddress, Keccak hash)
        {
            var txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractMerkleTree.InsertLeafAbiSig, hash);

            Transaction tx = new Transaction();
            tx.Value = 0;
            tx.Data = txData;
            tx.To = contractAddress;
            tx.SenderAddress = address;
            tx.GasLimit = 1000000;
            tx.GasPrice = 0.GWei();

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);
            // getReceipt check

            // FilterStore store = new FilterStore();

            // IEnumerable<string> topics = new string[] { "insertLeaf", "insertLeaves"};

            // LogFilter logFilter = store.CreateLogFilter(new BlockParameter(1), new BlockParameter(2), new AddressFilter(contractAddress));

            //var logs = _logFinder.FindLogs(logFilter);

            // foreach(var log in logs)
            // {
            //     Console.WriteLine("test");
            //     Console.WriteLine(log);
            // }

            filterLog = new FilterLog(0, 
                0, 
                5, 
                new Keccak("0xbbf3682375dae572acfb63c67f862dcdf59e96e043d44152cca7ebefa8c14cec"), 
                0,
                new Keccak("0xbe45ba4ec5fdfa14239c5e345f7e99dc7f7a6d6cd05e7e52b1fc5254bc712b9b"),
                new Address("0x83c82edd1605ac37d9065d784fdc000b20e9879d"),
                new byte[] { 
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 
                    242, 54, 130, 226, 242, 233, 234, 20, 29, 70, 99,
                    222, 252, 64, 247, 42, 118, 195, 91, 53, 216, 202, 
                    214, 224, 22, 25, 1, 242, 169, 103, 201, 182, 26, 
                    206, 48, 45, 79, 206, 116, 147, 119, 56, 32, 221, 
                    42, 126, 203, 132, 161, 108, 25, 159, 242, 96, 
                    122, 247, 122, 223, 240, 0, 0, 0, 0, 0 },
                new Keccak[] { new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0")}
            );

            // var leafHash = Bytes.ToHexString(filterLog.Data).Substring(64, 64);
            // 31- tree depth, leafCount
            var leafCount = 0;
            
            byte[] key = Rlp.Encode(Rlp.Encode(31), Rlp.Encode(leafCount)).Bytes;

            _memdb[key] = filterLog.Data.Slice(64, 64);

            byte[] retrievedBytes = _memdb[key];

            Console.WriteLine(Bytes.ToHexString(retrievedBytes));

            return ResultWrapper<Keccak>.Success(txHash);
        }
        public ResultWrapper<Keccak> baseline_insertLeaves(Address address, Address contractAddress)
        {
            var txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ContractMerkleTree.InsertLeavesAbiSig);

            Transaction tx = new Transaction();

            tx.Value = 0;
            tx.Data = txData;
            tx.To = contractAddress;
            tx.SenderAddress = address;
            tx.GasLimit = 1000000;
            tx.GasPrice = 0.GWei();

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            return ResultWrapper<Keccak>.Success(txHash);
        }

        public ResultWrapper<Keccak> baseline_deploy(Address address, string contractType)
        {

            try {
                ContractType c = new ContractType();

                Transaction tx = new Transaction();
                tx.Value = 0;
                tx.Init = Bytes.FromHexString(c.GetContractBytecode(contractType));
                tx.GasLimit = 2000000;
                tx.GasPrice = 20.GWei();
                tx.SenderAddress = address;

                Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);
                
                _logger.Info($"Sent transaction at price {tx.GasPrice} to {tx.SenderAddress}");
                _logger.Info($"Contract {contractType} has been deployed");

                return ResultWrapper<Keccak>.Success(txHash);
            } catch (System.IO.FileNotFoundException)
            {
                return ResultWrapper<Keccak>.Fail($"The given contract {contractType} does not exist.");
            } catch (Exception)
            {
                return ResultWrapper<Keccak>.Fail($"Error while while trying to deploy contract {contractType}.");
            } 
        }

        public ResultWrapper<string> baseline_getSiblings()
        {

            var tree = MerkleTree.CalculateMerkleTreeFromLeaves(_memdb.GetAllValues());
            return ResultWrapper<string>.Success("test3");
        }
    }
}