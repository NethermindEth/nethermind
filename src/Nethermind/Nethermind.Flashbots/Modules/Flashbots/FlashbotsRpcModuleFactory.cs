// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Flashbots.Handlers;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Flashbots.Modules.Flashbots
{
    public class FlashbotsRpcModuleFactory(
        ValidateSubmissionHandler validateSubmissionHandler
    ) : ModuleFactoryBase<IFlashbotsRpcModule>
    {
        private readonly ValidateSubmissionHandler _validateSubmissionHandler = validateSubmissionHandler ?? throw new ArgumentNullException(nameof(validateSubmissionHandler));

        public override IFlashbotsRpcModule Create()
        {
            return new FlashbotsRpcModule(_validateSubmissionHandler);
        }
    }
}
