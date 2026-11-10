// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.TxPool;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(
        typeof(InitializeBlockTree),
        typeof(SetupKeyStore)
    )]
    public class InitializeBlockchain(INethermindApi api) : IStep
    {
        private readonly INethermindApi _api = api;

        public async Task Execute(CancellationToken _) => await InitBlockchain();

        [Todo(Improve.Refactor, "Use chain spec for all chain configuration")]
        protected virtual Task InitBlockchain()
        {
            IBlocksConfig blocksConfig = _api.Config<IBlocksConfig>();

            ThisNodeInfo.AddInfo("Gaslimit     :", $"{blocksConfig.TargetBlockGasLimit:N0}");
            ThisNodeInfo.AddInfo("ExtraData    :", Utf8.IsValid(blocksConfig.GetExtraDataBytes()) ?
                blocksConfig.ExtraData :
                "- binary data -");

            // Resolved eagerly so the pool follows the head before network and block processing start.
            _api.Context.Resolve<ITxPool>();

            return Task.CompletedTask;
        }
    }
}
