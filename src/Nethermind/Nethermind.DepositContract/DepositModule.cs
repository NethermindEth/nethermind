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
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
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
        private readonly IDepositConfig _depositConfig;
        private readonly ILogger _logger;
        private readonly AbiDefinition _abiDefinition;
        private DepositContract? _depositContract;
        
        private AbiDefinitionParser _parser = new AbiDefinitionParser();

        public DepositModule(ITxPoolBridge txPoolBridge, IDepositConfig depositConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<DepositModule>() ?? throw new ArgumentNullException(nameof(logManager));
            _txPoolBridge = txPoolBridge ?? throw new ArgumentNullException(nameof(txPoolBridge));
            _depositConfig = depositConfig ?? throw new ArgumentNullException(nameof(depositConfig));
            _abiDefinition = _parser.Parse(File.ReadAllText("contracts/validator_registration.json"));

            if (depositConfig.DepositContractAddress != null)
            {
                var address = new Address(depositConfig.DepositContractAddress);
                _depositContract = new DepositContract(_abiDefinition, new AbiEncoder(), address);
            }
        }

        public ValueTask<ResultWrapper<Keccak>> deposit_deploy(Address senderAddress)
        {
            Transaction tx = new Transaction();
            tx.Value = 0;
            tx.Init = _abiDefinition.Bytecode;
            tx.GasLimit = 2000000;
            tx.GasPrice = 20.GWei();
            tx.SenderAddress = senderAddress;

            Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            if(_logger.IsInfo) _logger.Info($"Sent transaction at price {tx.GasPrice} to {tx.SenderAddress}");

            return new ValueTask<ResultWrapper<Keccak>>(ResultWrapper<Keccak>.Success(txHash));
        }

        public ValueTask<ResultWrapper<bool>> deposit_setContractAddress(Address contractAddress)
        {
            _depositConfig.DepositContractAddress = contractAddress.ToString();
            _depositContract = new DepositContract(_abiDefinition, new AbiEncoder(), contractAddress);
            return new ValueTask<ResultWrapper<bool>>(ResultWrapper<bool>.Success(true));
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

            byte[] amount = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(amount, (ulong) ((BigInteger)32.Ether() / (BigInteger)1.GWei()));

            /* what follows is SSZ merkleization
               we can probably use the code from Eth2 abstraction */
            
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
    }
}