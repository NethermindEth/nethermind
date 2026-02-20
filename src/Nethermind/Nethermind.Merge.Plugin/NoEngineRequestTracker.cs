// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;

namespace Nethermind.Merge.Plugin;

public class NoEngineRequestsTracker : IEngineRequestsTracker
{
    public void OnForkchoiceUpdatedCalled() { }

    public void OnNewPayloadCalled() { }

    public Task StartAsync()
        => Task.CompletedTask;
}
