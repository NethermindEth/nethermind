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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Baseline;
using Nethermind.Dirichlet.Numerics;
using Nethermind.TxPool;
using Nethermind.Facade;

namespace Nethermind.JsonRpc.Modules.Baseline
{
    public class BaselineModule : IBaselineModule
    {

        private readonly ILogger _logger;
        private readonly IBlockchainBridge _blockchainBridge;

        public BaselineModule(IBlockchainBridge blockchainBridge, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
        }
        public ResultWrapper<string> baseline_addLeaf()
        {
            return ResultWrapper<string>.Success("test");
        }
        public ResultWrapper<string> baseline_addLeaves()
        {
            return ResultWrapper<string>.Success("test1");
        }

        public ResultWrapper<Keccak> baseline_deploy(Address address)
        {
            Transaction tx = new Transaction();
            tx.Value = 0;
            tx.Init = Bytes.FromHexString("0x6080604052348015600f57600080fd5b5060ac8061001e6000396000f3fe6080604052348015600f57600080fd5b506004361060325760003560e01c806360fe47b11460375780636d4ce63c146053575b600080fd5b605160048036036020811015604b57600080fd5b5035606b565b005b60596070565b60408051918252519081900360200190f35b600055565b6000549056fea26469706673582212207415a78c1f0052dcb4a8dddd182e39a37e7b647b50133c6c335783222b70ef0364736f6c63430006040033");
            tx.GasLimit = 2000000;
            tx.GasPrice = 20.GWei();
            tx.SenderAddress = address;

            Keccak txHash = _blockchainBridge.SendTransaction(tx, TxHandlingOptions.ManagedNonce);

            _logger.Info($"Sent transaction at price {tx.GasPrice}");

            return ResultWrapper<Keccak>.Success(txHash);
        }

        public ResultWrapper<string> baseline_getSiblings()
        {
            return ResultWrapper<string>.Success("test3");
        }
    }
}