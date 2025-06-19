// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL.ArgumentBundle;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System;

namespace Nethermind.Evm.CodeAnalysis.IL;

public delegate ref Word ILEmittedInternalMethod(
    ref ILChunkExecutionArguments iLChunkExecutionArguments,
    ITxTracer tracer,
    ILogger logger,
    ref ILChunkExecutionState result);

