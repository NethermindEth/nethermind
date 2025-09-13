// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac.Core;

namespace Nethermind.Api.Extensions;

public interface INethermindPlugin
{
    string Name { get; }

    string Description { get; }

    string Author { get; }

    void InitTxTypesAndRlpDecoders(INethermindApi api) { }

    Task Init(INethermindApi nethermindApi) => Task.CompletedTask;

    Task InitNetworkProtocol() => Task.CompletedTask;

    Task InitRpcModules() => Task.CompletedTask;
    bool MustInitialize => false;
    bool Enabled { get; }
    IModule? Module => null;

    int Priority => PluginPriorities.Default;
}
