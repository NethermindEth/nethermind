// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac.Core;

namespace Nethermind.Api.Extensions
{
    public interface INethermindPlugin : IAsyncDisposable
    {
        string Name { get; }

        string Description { get; }

        string Author { get; }

        bool Enabled { get; }

        IModule? Module => null;

        Task Init(INethermindApi nethermindApi) => Task.CompletedTask;

        Task InitNetworkProtocol() => Task.CompletedTask;

        Task InitRpcModules() => Task.CompletedTask;

        bool MustInitialize { get => false; }
    }
}
