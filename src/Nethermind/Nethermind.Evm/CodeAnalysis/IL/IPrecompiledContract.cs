// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL;

public interface IPrecompiledContract
{
    public bool MoveNext(
        EvmState env,
        ref long gasAvailable,
        ref int programCounter,
        ref int stackHead, ref Word stackHeadRef,
        ref ReadOnlyMemory<byte> returnDataBuffer,
        ITxTracer tracer, ILogger logger,
        ref ILChunkExecutionState state); // it returns true if current staet is HALTED or FINISHED and Sets Current.CallResult in case of CALL or CREATE

}
