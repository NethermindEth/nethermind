// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IBlockGasLimitContract : IActivatedAtBlock
    {
        UInt256? BlockGasLimit(BlockHeader parentHeader);
    }

    public sealed class BlockGasLimitContract : Contract, IBlockGasLimitContract
    {
        private IConstantContract Constant { get; }
        public long Activation { get; }

        public BlockGasLimitContract(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            long transitionBlock,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            Activation = transitionBlock;
            Constant = GetConstant(readOnlyTxProcessorSource);
        }

        public UInt256? BlockGasLimit(BlockHeader parentHeader)
        {
            this.BlockActivationCheck(parentHeader);
            var function = nameof(BlockGasLimit);
            var returnData = Constant.Call(new CallInfo(parentHeader, function, Address.Zero));
            return (returnData?.Length ?? 0) == 0 ? (UInt256?)null : (UInt256)returnData[0];
        }
    }
}
