// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Tracing;
using static Nethermind.Core.Extensions.MemoryExtensions;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm.TransactionProcessing
{
    public class GethSystemTxProcessor : AuraSystemTxProcessor
    {
        public GethSystemTxProcessor(ISpecProvider? specProvider, IWorldState? worldState, IVirtualMachine? virtualMachine, EthereumEcdsa? ecdsa, ILogger? logger)
            : base(specProvider, worldState, virtualMachine, ecdsa, logger)
        {
        }

        protected override void GetSpecFromHeader(BlockExecutionContext blCtx, out BlockHeader header, out IReleaseSpec spec)
        {
            header = blCtx.Header;
            spec = SpecProvider.GetSpec(header);
        }

        // TODO Should we remove this already
        protected override bool RecoverSenderIfNeeded(Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
        {
            return false;
        }
    }
}


