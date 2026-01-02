// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Producers;

namespace Nethermind.JsonRpc.Modules.Evm
{
    public class EvmRpcModule : IEvmRpcModule
    {
        private readonly IManualBlockProductionTrigger _trigger;

        public EvmRpcModule(IManualBlockProductionTrigger? trigger)
        {
            _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        }

        public ResultWrapper<bool> evm_mine()
        {
            _trigger.BuildBlock();
            return ResultWrapper<bool>.Success(true);
        }
    }
}
