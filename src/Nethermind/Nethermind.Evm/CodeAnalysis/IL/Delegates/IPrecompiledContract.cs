// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL.ArgumentBundle;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System;

namespace Nethermind.Evm.CodeAnalysis.IL.Delegates;

public delegate bool ILEmittedMethod(
    ref ILChunkExecutionArguments envArg, 
    ref ILChunkExecutionState result);

