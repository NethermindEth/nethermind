// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class BlockCallOutputTracer : BlockTracer
    {
        private readonly Dictionary<Keccak, CallOutputTracer> _results = new();
        public override ITxTracer StartNewTxTrace(Transaction? tx) => _results[tx?.Hash ?? Keccak.Zero] = new CallOutputTracer();
        public IReadOnlyDictionary<Keccak, CallOutputTracer> BuildResults() => _results;
    }
}
