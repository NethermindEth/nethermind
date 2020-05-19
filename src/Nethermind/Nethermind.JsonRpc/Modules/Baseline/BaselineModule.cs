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

namespace Nethermind.JsonRpc.Modules.Baseline
{
    public class BaselineModule : IBaselineModule
    {

        private readonly ILogger _logger;
        private readonly ITxPoolBridge _txPoolBridge;

        public BaselineModule(ITxPoolBridge txPoolBridge, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txPoolBridge = txPoolBridge ?? throw new ArgumentNullException(nameof(txPoolBridge));
        }
        public ResultWrapper<string> baseline_addLeaf()
        {
            return ResultWrapper<string>.Success("test");
        }
        public ResultWrapper<string> baseline_addLeaves()
        {
            return ResultWrapper<string>.Success("test1");
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
            } catch (ArgumentNullException)
            {
                return ResultWrapper<Keccak>.Fail($"The given contract {contractType} does not exist.");
            } catch (Exception)
            {
                return ResultWrapper<Keccak>.Fail($"Error while while trying to deply contract {contractType}.");
            } 
        }

        public ResultWrapper<string> baseline_getSiblings()
        {
            return ResultWrapper<string>.Success("test3");
        }
    }
}