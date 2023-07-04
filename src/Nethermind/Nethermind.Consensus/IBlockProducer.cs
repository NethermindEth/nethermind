// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Consensus;

public interface IBlockProducer
{
    Task Start();
    Task StopAsync();
    bool IsProducingBlocks(ulong? maxProducingInterval);
    event EventHandler<BlockEventArgs> BlockProduced;
}
