// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System;

namespace Nethermind.Evm.CodeAnalysis.IL;

public delegate ref Word ILEmittedInternalMethod(
        in byte machineCodeRef,

        IReleaseSpec spec,
        ISpecProvider specProvider,
        IBlockhashProvider blockhashProvider,
        ICodeInfoRepository codeInfoProvider,
        IWorldState state,

        EvmState vmState,
        ref ExecutionEnvironment env,
        ref TxExecutionContext txCtx,
        ref BlockExecutionContext blkCtx,

        ReadOnlyMemory<byte> returnDataBufffer,

        ref long gasAvailable,
        ref int programCounter,
        ref int stackHead,
        ref Word stackHeadRef,

        ITxTracer tracer,
        ILogger logger,
        ref ILChunkExecutionState result);

