// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Db;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.TraceStore;

public class TraceStoreModuleFactory(IRpcModuleFactory<ITraceRpcModule> innerFactory,
    IDb traceStore,
    IBlockFinder blockFinder,
    IReceiptFinder receiptFinder,
    ITraceSerializer<ParityLikeTxTrace> traceSerializer,
    ILogManager logManager,
    int parallelization = 0) : ModuleFactoryBase<ITraceRpcModule>
{
    private readonly IRpcModuleFactory<ITraceRpcModule> _innerFactory = innerFactory;
    private readonly IDb _traceStore = traceStore;
    private readonly IBlockFinder _blockFinder = blockFinder;
    private readonly IReceiptFinder _receiptFinder = receiptFinder;
    private readonly ITraceSerializer<ParityLikeTxTrace> _traceSerializer = traceSerializer;
    private readonly ILogManager _logManager = logManager;
    private readonly int _parallelization = parallelization;

    public override ITraceRpcModule Create() => new TraceStoreRpcModule(_innerFactory.Create(), _traceStore, _blockFinder, _receiptFinder, _traceSerializer, _logManager, _parallelization);
}
