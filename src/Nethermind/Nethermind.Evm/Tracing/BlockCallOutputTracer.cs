// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing
{
    public class BlockCallOutputTracer : BlockTracer
    {
        private readonly Dictionary<Hash256, CallOutputTracer> _results = new();
        public override ITxTracer StartNewTxTrace(Transaction? tx) => _results[tx?.Hash ?? Keccak.Zero] = new CallOutputTracer();
        public IReadOnlyDictionary<Hash256, CallOutputTracer> BuildResults() => _results;
    }
}
