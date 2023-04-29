// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Abi;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
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
        IServiceProvider Services { get; set; }
        DisposableStack DisposeStack { get; }

        IAbiEncoder AbiEncoder { get; }
        ChainSpec? ChainSpec { get; set; }
        IConfigProvider ConfigProvider { get; set; }
        ICryptoRandom CryptoRandom { get; }
        IDbProvider? DbProvider { get; set; }
        IRocksDbFactory? RocksDbFactory { get; set; }
        IMemDbFactory? MemDbFactory { get; set; }
        IEthereumEcdsa? EthereumEcdsa { get; set; }
        IJsonSerializer EthereumJsonSerializer { get; set; }
        IFileSystem FileSystem { get; set; }
        IKeyStore? KeyStore { get; set; }
        ILogManager LogManager { get; set; }
        ProtectedPrivateKey? OriginalSignerKey { get; set; }
        IReadOnlyList<INethermindPlugin> Plugins { get; }
        string SealEngineType { get; set; }
        ISpecProvider? SpecProvider { get; set; }
        ISyncModeSelector? SyncModeSelector { get; set; }
        ISyncProgressResolver? SyncProgressResolver { get; set; }
        IBetterPeerStrategy? BetterPeerStrategy { get; set; }
        ITimestamper Timestamper { get; }
        ITimerFactory TimerFactory { get; }
        IProcessExitSource ProcessExit { get; set; }

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
