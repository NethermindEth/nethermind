// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Consensus;

public interface IBlockProducerRunner : IStoppableService
{
    void Start();
    string IStoppableService.Description => "block producer";
    bool IsProducingBlocks(ulong? maxProducingInterval);
    event EventHandler<BlockEventArgs> BlockProduced;
}
