// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;

namespace Nethermind.JsonRpc.Modules.Evm
{
    public class EvmRpcModule : IEvmRpcModule
    {
        private readonly IBlockProducer _blockProducer;

        public EvmRpcModule(IBlockProducer? blockProducer)
        {
            _blockProducer = blockProducer ?? throw new ArgumentNullException(nameof(blockProducer));
        }

        public ResultWrapper<bool> evm_mine()
        {
            _blockProducer.BuildBlock();
            return ResultWrapper<bool>.Success(true);
        }
    }
}
