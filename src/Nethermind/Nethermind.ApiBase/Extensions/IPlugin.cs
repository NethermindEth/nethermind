// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.ApiBase.Extensions;

public interface INethermindPlugin : IAsyncDisposable
{
    string Name { get; }

    string Description { get; }

    string Author { get; }

    Task InitNetworkProtocol();

    Task InitRpcModules();

    bool MustInitialize { get => false; }
}

public interface INethermindPlugin<TIApi> : IAsyncDisposable, INethermindPlugin
    where TIApi : IBasicApiWithPlugins
{
    Task Init(TIApi nethermindApi);
}
