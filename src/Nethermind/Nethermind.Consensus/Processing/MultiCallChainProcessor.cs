// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Processing;

public class MultiCallChainProcessor : OneTimeChainProcessor
{
    public MultiCallChainProcessor(IReadOnlyDbProvider readOnlyDbProvider, IBlockchainProcessor processor) : base(readOnlyDbProvider, processor)
    {
    }

    public override Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer)
    {
        lock (_lock)
        {
            Block result;
            result = _processor.Process(block, options, tracer);
            return result;
        }
    }

    public override void Dispose()
    {
        _readOnlyDbProvider.ClearTempChanges();
        base.Dispose();
    }
}
