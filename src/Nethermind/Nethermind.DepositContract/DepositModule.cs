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

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.DepositContract
{
    public class DepositModule : IDepositModule
    {
        private readonly ITxPoolBridge _txPoolBridge;
        private readonly ILogFinder _logFinder;
        private readonly IDepositConfig _depositConfig;
        private readonly ILogger _logger;
        private DepositContract? _depositContract;

        public DepositModule(ITxPoolBridge txPoolBridge, ILogFinder logFinder, IDepositConfig depositConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<DepositModule>() ?? throw new ArgumentNullException(nameof(logManager));
            _txPoolBridge = txPoolBridge ?? throw new ArgumentNullException(nameof(txPoolBridge));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _depositConfig = depositConfig ?? throw new ArgumentNullException(nameof(depositConfig));
            
            if (!string.IsNullOrEmpty(depositConfig.DepositContractAddress))
            {
                var address = new Address(depositConfig.DepositContractAddress);
                _depositContract = new DepositContract(new AbiEncoder(), address);
            }
        }

        public ValueTask<ResultWrapper<Keccak>> deposit_deploy(Address senderAddress)
        {
            ResultWrapper<Keccak> result;
            
            if (_depositContract == null)
            {
                result = ResultWrapper<Keccak>.Fail("Deposit contract address not specified.", ErrorCodes.InternalError);
                return new ValueTask<ResultWrapper<Keccak>>(result);    
            }

            Transaction tx = _depositContract.Deploy(senderAddress);
            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            if(_logger.IsInfo) _logger.Info($"Sent transaction at price {tx.GasPrice} to {tx.SenderAddress}");

            result = ResultWrapper<Keccak>.Success(txHash);
            return new ValueTask<ResultWrapper<Keccak>>(result);
        }

        public ValueTask<ResultWrapper<bool>> deposit_setContractAddress(Address contractAddress)
        {
            _depositConfig.DepositContractAddress = contractAddress.ToString();
            _depositContract = new DepositContract(new AbiEncoder(), contractAddress);
            return new ValueTask<ResultWrapper<bool>>(ResultWrapper<bool>.Success(true));
        }

        public class DepositData
        {
            public long BlockNumber { get; set; }
            public long TxIndex { get; set; }
            public long LogIndex { get; set; }
            
            public byte[] PubKey { get; set; }
            public byte[] WithdrawalCredentials { get; set; }
            public byte[] Amount { get; set; }
            public byte[] BlsSignature { get; set; }
        }
        
        public ValueTask<ResultWrapper<DepositData[]>> deposit_getAll()
        {
            ResultWrapper<DepositData[]> result;
            if (_depositContract == null)
            {
                result = ResultWrapper<DepositData[]>.Fail("Deposit contract address not specified.", ErrorCodes.InternalError);
                return new ValueTask<ResultWrapper<DepositData[]>>(result);    
            }

            var logFilter = new LogFilter(
                1,
                new BlockParameter(0L),
                BlockParameter.Latest,
                new AddressFilter(_depositContract.ContractAddress),
                new TopicsFilter(new SpecificTopic(_depositContract.DepositEventHash)));
            
            var logs = _logFinder.FindLogs(logFilter);
            List<DepositData> allData = new List<DepositData>();
            foreach (FilterLog filterLog in logs)
            {
                DepositData depositData = new DepositData();
                depositData.LogIndex = filterLog.LogIndex;
                depositData.TxIndex = filterLog.TransactionIndex;
                depositData.BlockNumber = filterLog.BlockNumber;
                depositData.Amount = filterLog.Data.Slice(352, 8);
                depositData.PubKey = filterLog.Data.Slice(192, 48);
                depositData.WithdrawalCredentials = filterLog.Data.Slice(288, 32);
                depositData.BlsSignature = filterLog.Data.Slice(416, 96);
                allData.Add(depositData);
            }

            // foreach log

            result = ResultWrapper<DepositData[]>.Success(allData.ToArray());
            return new ValueTask<ResultWrapper<DepositData[]>>(result);
        }
        
        public ValueTask<ResultWrapper<Keccak>> deposit_make(
            Address senderAddress,
            byte[] blsPublicKey,
            byte[] withdrawalCredentials,
            byte[] blsSignature)
        {
            if (_depositContract == null)
            {
                var result = ResultWrapper<Keccak>.Fail("Deposit contract address not specified.", ErrorCodes.InternalError);
                return new ValueTask<ResultWrapper<Keccak>>(result);    
            }

            var depositDataRoot = CalculateDepositDataRoot(blsPublicKey, withdrawalCredentials, blsSignature);

            Transaction tx = _depositContract.Deposit(
                senderAddress,
                blsPublicKey,
                withdrawalCredentials,
                blsSignature,
                depositDataRoot);
            
            tx.Value = 32.Ether();
            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            return new ValueTask<ResultWrapper<Keccak>>(ResultWrapper<Keccak>.Success(txHash));
        }

        /// <summary>
        /// Calculates merkleized SSZ root
        /// </summary>
        /// <param name="blsPublicKey"></param>
        /// <param name="withdrawalCredentials"></param>
        /// <param name="blsSignature"></param>
        /// <returns></returns>
        private static byte[] CalculateDepositDataRoot(
            byte[] blsPublicKey,
            byte[] withdrawalCredentials,
            byte[] blsSignature)
        {
            byte[] amount = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(amount, (ulong) ((BigInteger)32.Ether() / (BigInteger)1.GWei()));
            
            var sha256 = SHA256.Create();
            byte[] zeroBytes32 = new byte[32];
            byte[] pubKeyInput = new byte[64];
            blsPublicKey.AsSpan().CopyTo(pubKeyInput.AsSpan(0, 48));
            byte[] pubKeyRoot = sha256.ComputeHash(pubKeyInput);
            byte[] signatureRoot =
                sha256.ComputeHash(
                    Bytes.Concat(
                        sha256.ComputeHash(blsSignature.Slice(0, 64)),
                        sha256.ComputeHash(
                            Bytes.Concat(
                                blsSignature.Slice(64, 32),
                                zeroBytes32))));

            byte[] depositDataRoot = sha256.ComputeHash(
                Bytes.Concat(
                    sha256.ComputeHash(Bytes.Concat(pubKeyRoot, withdrawalCredentials)),
                    sha256.ComputeHash(Bytes.Concat(amount.PadRight(32), signatureRoot))));
            return depositDataRoot;
        }
    }
}
