// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Consensus;

public class NoBlockProducerRunner : IBlockProducerRunner
{
    public Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public void Start()
    {
    }

    public bool IsProducingBlocks(ulong? maxProducingInterval)
    {
        return false;
    }

    public event EventHandler<BlockEventArgs>? BlockProduced { add { } remove { } }
}
