// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Autofac;
using Nethermind.Abi;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Api
{
    public interface IBasicApi
    {
        IDisposableStack DisposeStack { get; }

        IAbiEncoder AbiEncoder { get; }
        [SkipServiceCollection]
        ChainSpec ChainSpec { get; }

        [SkipServiceCollection]
        IConfigProvider ConfigProvider { get; }
        ICryptoRandom CryptoRandom { get; }
        IDbProvider? DbProvider { get; set; }
        IDbFactory? DbFactory { get; set; }
        IEthereumEcdsa? EthereumEcdsa { get; set; }
        [SkipServiceCollection]
        IJsonSerializer EthereumJsonSerializer { get; }
        IFileSystem FileSystem { get; set; }
        IKeyStore? KeyStore { get; set; }
        [SkipServiceCollection]
        ILogManager LogManager { get; }
        [SkipServiceCollection]
        IProtectedPrivateKey? OriginalSignerKey { get; set; }
        IReadOnlyList<INethermindPlugin> Plugins { get; }
        [SkipServiceCollection]
        string SealEngineType { get; }
        [SkipServiceCollection]
        ISpecProvider? SpecProvider { get; }
        ITimestamper Timestamper { get; }
        ITimerFactory TimerFactory { get; }
        IProcessExitSource? ProcessExit { get; }

        [SkipServiceCollection]
        ILifetimeScope Context { get; }

        public IConsensusPlugin? GetConsensusPlugin() =>
            Plugins
                .OfType<IConsensusPlugin>()
                .SingleOrDefault();

        public IEnumerable<IConsensusWrapperPlugin> GetConsensusWrapperPlugins() =>
            Plugins.OfType<IConsensusWrapperPlugin>().Where(static p => p.Enabled);
    }
}
