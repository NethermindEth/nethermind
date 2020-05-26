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
            // return ResultWrapper<string>.Success("test3");
            return null;
        }
    }
}