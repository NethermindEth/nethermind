// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing
{
    public interface IBlockchainProcessor : IAsyncDisposable
    {
        Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default);
    }
}
