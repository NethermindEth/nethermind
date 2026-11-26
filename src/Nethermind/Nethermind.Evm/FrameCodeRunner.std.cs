// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public abstract partial class FrameCodeRunner<TGasPolicy>
{
    /// <summary>The opaque per-code slot a runner keeps its state in: one per code, shared by every machine and thread running it.</summary>
    /// <remarks>Core never reads or writes it. Publish into it with <see cref="System.Threading.Interlocked.CompareExchange{T}(ref T, T, T)"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static ref object? CodeState(CodeInfo codeInfo) => ref codeInfo.FrameCodeRunnerState;

    /// <summary>What each entry of <paramref name="spec"/>'s untraced dispatch table runs.</summary>
    /// <remarks>The first call for a spec instance builds a table, which takes tens of microseconds.</remarks>
    protected static OpcodeBodySet GetOpcodeBodySet(IReleaseSpec spec) => VirtualMachine<TGasPolicy>.GetOpcodeBodySet(spec);

    /// <summary>Polls cancellation as the dispatch loop does, throwing the loop's own exception.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static void PollCancellation(VirtualMachine<TGasPolicy> vm)
    {
        if (vm.TxTracer.IsCancelled)
            VirtualMachineStatics.ThrowOperationCanceledException();
    }

    /// <summary>The opcodes after which the dispatch loop polls at its next taken jump.</summary>
    protected static int CancellationPollInterval => VirtualMachine<TGasPolicy>.CancellationPollInterval;
}
