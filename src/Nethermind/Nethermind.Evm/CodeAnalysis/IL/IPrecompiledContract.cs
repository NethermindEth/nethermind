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

public unsafe delegate bool ILExecutionStep(
        in byte machineCodeRef,
        ISpecProvider specProvider,
        IBlockhashProvider blockhashProvider,
        ICodeInfoRepository codeInfoProvider,
        EvmState env,
        IWorldState state,
        ref long gasAvailable,
        ref int programCounter,
        ref int stackHead,
        ref Word stackHeadRef,
        ITxTracer tracer,
        ILogger logger,
        ref ILChunkExecutionState result);
