// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Tracing;

namespace Nethermind.Evm.Tracing;

public interface IILVMTracer
{

    /// <summary>
    /// Traces EVM chunks
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportPredefinedPatternExecution"/>
    /// </remarks>
    bool IsTracingPredefinedPatterns { get; }

    /// <summary>
    /// Traces EVM precompiled segments
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportCompiledSegmentExecution"/>
    /// </remarks>
    bool IsTracingCompiledSegments { get; }

    void ReportPredefinedPatternExecution(long gas, int pc, string segmentID, in ExecutionEnvironment env);
    void ReportCompiledSegmentExecution(long gas, int pc, string segmentId, in ExecutionEnvironment env);

}
