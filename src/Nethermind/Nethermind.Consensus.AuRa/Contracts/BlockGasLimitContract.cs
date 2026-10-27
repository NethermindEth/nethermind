// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Experimental.Abi;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IBlockGasLimitContract : IActivatedAtBlock
    {
        UInt256? BlockGasLimit(BlockHeader parentHeader);
    }

    public sealed class BlockGasLimitContract : Contract, IBlockGasLimitContract
    {
        private static readonly ContractFunction<ValueTuple, UInt256> BlockGasLimitFunction = new AbiSignature("blockGasLimit").With().Returns(AbiType.UInt256);

        private IConstantContract Constant { get; }
        public ulong Activation { get; }

        public BlockGasLimitContract(
            Address contractAddress,
            ulong transitionBlock,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
            : base(contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            Activation = transitionBlock;
            Constant = GetConstant(readOnlyTxProcessorSource);
        }

        public UInt256? BlockGasLimit(BlockHeader parentHeader)
        {
            this.BlockActivationCheck(parentHeader);
            return Constant.Call(parentHeader, BlockGasLimitFunction, Address.Zero, default);
        }
    }
}
