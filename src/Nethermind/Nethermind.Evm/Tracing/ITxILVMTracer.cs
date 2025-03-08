// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
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
    bool IsTracingIlEvmCalls { get; }
    void ReportIlEvmChunkExecution(long gas, int pc, string segmentID, in ExecutionEnvironment env);

}
