// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(ApplyMemoryHint))]
    public sealed class InitTxTypesAndRlp(INethermindApi api) : IStep
    {
        public Task Execute(CancellationToken _)
        {
            // Build the decoder registry from scratch using the builder.
            RlpDecoderRegistryBuilder builder = new();

            // Register all decoders from the base RLP assembly.
            builder.RegisterDecoders(Assembly.GetAssembly(typeof(Rlp)));

            // Register TxDecoder explicitly (it uses SkipGlobalRegistration).
            builder.RegisterDecoder(typeof(Transaction), TxDecoder.Instance);

            // Register decoders from the Network assembly.
            Assembly? networkAssembly = Assembly.GetAssembly(typeof(NetworkNodeDecoder));
            builder.RegisterDecoders(networkAssembly);

            // Let plugins contribute their decoders to the builder.
            foreach (INethermindPlugin plugin in api.Plugins)
            {
                plugin.InitTxTypesAndRlpDecoders(api, builder);
            }

            // Freeze the registry. All DI-resolved components will use this immutable snapshot.
            Rlp.DefaultRegistry = builder.Build();

            return Task.CompletedTask;
        }
    }
}
