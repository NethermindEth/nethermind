// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;

namespace Nethermind.BlockValidation;

public class BlockValidation: INethermindPlugin
{
    public virtual string Name => "BlockValidation";
    public virtual string Description => "BlockValidation";
    public string Author => "Nethermind";
    public Task InitRpcModules()
    {
        return Task.CompletedTask;
    }

    public Task Init(INethermindApi api)
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}