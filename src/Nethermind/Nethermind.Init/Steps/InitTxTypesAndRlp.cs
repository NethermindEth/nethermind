// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ApplyMemoryHint))]
    public sealed class InitTxTypesAndRlp(INethermindApi api) : IStep
    {
        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        public Task Execute(CancellationToken _)
        {
            Assembly? assembly = Assembly.GetAssembly(typeof(NetworkNodeDecoder));
            if (assembly is not null)
            {
                Rlp.RegisterDecoders(assembly);
            }

            foreach (INethermindPlugin plugin in api.Plugins)
            {
                plugin.InitTxTypesAndRlpDecoders(api);
            }

            // The DI block decoders resolve EIP-7668 activation, which the scanned defaults cannot. Only a global header
            // decoder of the same type is replaced, so a plugin that registers its own decoder only globally keeps it.
            IHeaderDecoder headerDecoder = api.Context.Resolve<IHeaderDecoder>();
            if (Rlp.GetDecoder<BlockHeader>()?.GetType() == headerDecoder.GetType())
            {
                Rlp.RegisterDecoder(typeof(BlockHeader), headerDecoder);
                Rlp.RegisterDecoder(typeof(Block), api.Context.Resolve<BlockDecoder>());
                Rlp.RegisterDecoder(typeof(BlockBody), api.Context.Resolve<BlockBodyDecoder>());
            }

            RlpLimit.InitMaxBlockGas(api.Config<IBlocksConfig>().MaxGasLimit);

            return Task.CompletedTask;
        }
    }
}
