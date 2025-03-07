// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api.Steps;

namespace Nethermind.Api.Extensions;

public interface INethermindPlugin : IAsyncDisposable
{
    string Name { get; }

    string Description { get; }

    string Author { get; }

    void InitTxTypesAndRlpDecoders(INethermindApi api) { }

    Task Init(INethermindApi nethermindApi) => Task.CompletedTask;

    Task InitNetworkProtocol() => Task.CompletedTask;

    Task InitRpcModules() => Task.CompletedTask;
    IEnumerable<StepInfo> GetSteps() => [];
    bool MustInitialize => false;
    bool Enabled { get; }
}
