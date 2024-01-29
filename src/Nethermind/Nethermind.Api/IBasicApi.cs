// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Autofac;
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
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Api
{
    public interface IBasicApi
    {
        DisposableStack DisposeStack { get; }

        ChainSpec ChainSpec { get; }
        IConfigProvider ConfigProvider { get; }
        ICryptoRandom CryptoRandom { get; }
        IDbProvider? DbProvider { get; }
        IEthereumEcdsa? EthereumEcdsa { get; }
        IJsonSerializer EthereumJsonSerializer { get; }
        IFileSystem FileSystem { get; }
        IKeyStore KeyStore { get; }
        ILogManager LogManager { get; }
        IReadOnlyList<INethermindPlugin> Plugins { get; }
        string SealEngineType { get; }
        ISpecProvider? SpecProvider { get; }
        ISyncModeSelector SyncModeSelector { get; set; }
        IBetterPeerStrategy? BetterPeerStrategy { get; set; }
        ITimestamper Timestamper { get; }
        ITimerFactory TimerFactory { get; }
        IProcessExitSource? ProcessExit { get; }

        // TODO: Eventually, no code should use this
        ILifetimeScope BaseContainer { get; }

        public IConsensusPlugin? GetConsensusPlugin() =>
            Plugins
                .OfType<IConsensusPlugin>()
                .SingleOrDefault(cp => cp.SealEngineType == SealEngineType);

        public IEnumerable<IConsensusWrapperPlugin> GetConsensusWrapperPlugins() =>
            Plugins.OfType<IConsensusWrapperPlugin>().Where(p => p.Enabled);

        public IEnumerable<ISynchronizationPlugin> GetSynchronizationPlugins() =>
            Plugins.OfType<ISynchronizationPlugin>();
    }
}
