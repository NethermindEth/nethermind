// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;

namespace Nethermind.Facade
{
    public abstract class BlockchainBridgeContract : Contract
    {
        public BlockchainBridgeContract(IAbiEncoder abiEncoder, Address contractAddress, AbiDefinition? abiDefinition = null) : base(abiEncoder, contractAddress, abiDefinition)
        {
        }

        /// <summary>
        /// Gets constant version of the contract. Allowing to call contract methods without state modification.
        /// </summary>
        /// <param name="blockchainBridge"><see cref="IBlockchainBridge"/> to call transactions.</param>
        /// <returns>Constant version of the contract.</returns>
        protected IConstantContract GetConstant(IBlockchainBridge blockchainBridge) =>
            new ConstantBridgeContract(this, blockchainBridge);

        private class ConstantBridgeContract : ConstantContractBase
        {
            private readonly IBlockchainBridge _blockchainBridge;

            public ConstantBridgeContract(Contract contract, IBlockchainBridge blockchainBridge)
                : base(contract)
            {
                _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            }

            public override object[] Call(CallInfo callInfo)
            {
                var transaction = GenerateTransaction(callInfo);
                var result = _blockchainBridge.Call(callInfo.ParentHeader, transaction, CancellationToken.None);
                if (!string.IsNullOrEmpty(result.Error))
                {
                    throw new AbiException(result.Error);
                }

                return callInfo.Result = DecodeReturnData(callInfo.FunctionName, result.OutputData);
            }
        }
    }
}
