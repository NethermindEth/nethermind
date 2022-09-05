//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.TraceStore;

public class TraceStoreModuleFactory : ModuleFactoryBase<ITraceRpcModule>
{
    private readonly IRpcModuleFactory<ITraceRpcModule> _innerFactory;
    private readonly IDbWithSpan _traceStore;
    private readonly IBlockFinder _blockFinder;
    private readonly IReceiptFinder _receiptFinder;
    private readonly ILogManager _logManager;

    public TraceStoreModuleFactory(IRpcModuleFactory<ITraceRpcModule> innerFactory, IDbWithSpan traceStore, IBlockFinder blockFinder, IReceiptFinder receiptFinder, ILogManager logManager)
    {
        _innerFactory = innerFactory;
        _traceStore = traceStore;
        _blockFinder = blockFinder;
        _receiptFinder = receiptFinder;
        _logManager = logManager;
    }

    public override ITraceRpcModule Create() => new TraceStoreRpcModule(_innerFactory.Create(), _traceStore, _blockFinder, _receiptFinder, _logManager);
}
