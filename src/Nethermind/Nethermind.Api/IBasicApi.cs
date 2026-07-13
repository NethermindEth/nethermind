// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Api
{
    public interface IBasicApi
    {
        IDisposableStack DisposeStack { get; }

        [SkipServiceCollection]
        ChainSpec ChainSpec { get; }

        [SkipServiceCollection]
        IConfigProvider ConfigProvider { get; }
        IDbProvider DbProvider { get; }
        IEthereumEcdsa EthereumEcdsa { get; }
        [SkipServiceCollection]
        EthereumJsonSerializer EthereumJsonSerializer { get; }
        IFileSystem FileSystem { get; }
        [SkipServiceCollection]
        ILogManager LogManager { get; }
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
    }
}
