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
    /// - <see cref="ReportChunkExecution"/>
    /// </remarks>
    bool IsTracingEvmChunks { get; }

    /// <summary>
    /// Traces EVM precompiled segments
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportCompiledSegmentExecution"/>
    /// </remarks>
    bool IsTracingEvmSegments { get; }

    void ReportChunkExecution(long gas, int pc, string segmentID);
    void ReportCompiledSegmentExecution(long gas, int pc, string segmentId);

}
