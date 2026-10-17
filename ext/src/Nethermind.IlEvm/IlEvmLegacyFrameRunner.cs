// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

/// <summary>
/// The Legacy rules' own runner (<see cref="IlEvmSettings.LegacyEntry"/>): a part finds its contract and runs it, as
/// <see cref="IlEvmFrameRunner"/>'s Legacy path does, from an entry that holds nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The general runner reaches the Legacy path through the mode's branches and a second, out-of-line call, which also
/// carries profiling and the diagnostics' timing. Here one read (<see cref="IlEvmSettings.LegacyDetour"/>) sends a part
/// under any other rules, or with the kill switch off or profiling on, to the general runner; a contract whose self-check
/// has not kept it compiled goes to <see cref="IlEvmFrameRunner.RunUnsettled"/>. It is attached only without diagnostics.
/// </para>
/// <para>
/// <typeparamref name="TBlock"/> is on for the block processing runner, off for the read-only queries'. One instance per
/// surface (<see cref="IlEvmSurface"/>), each counting its parts under its own (<see cref="IlSurfaceCounts"/>), as the
/// general runner's instances do; a detoured part is counted here, not again by the general runner.
/// </para>
/// </remarks>
public sealed class IlEvmLegacyFrameRunner<TBlock> : FrameCodeRunner<EthereumGasPolicy> where TBlock : struct, IFlag
{
    private static readonly IlEvmLegacyFrameRunner<TBlock> _call = new(IlEvmSurface.Call);
    private static readonly IlEvmLegacyFrameRunner<TBlock> _trace = new(IlEvmSurface.Trace);
    private static readonly IlEvmLegacyFrameRunner<TBlock> _simulate = new(IlEvmSurface.Simulate);
    private static readonly IlEvmLegacyFrameRunner<TBlock> _blocks = new(IlEvmSurface.Blocks);

    /// <summary>The block processing runner's instance, or the read-only call environments' one.</summary>
    public static IlEvmLegacyFrameRunner<TBlock> Instance => TBlock.IsActive ? _blocks : _call;

    /// <summary>The instance for <paramref name="surface"/>; block processing's runner has only <see cref="IlEvmSurface.Blocks"/>.</summary>
    public static IlEvmLegacyFrameRunner<TBlock> For(IlEvmSurface surface) => TBlock.IsActive ? _blocks : surface switch
    {
        IlEvmSurface.Trace => _trace,
        IlEvmSurface.Simulate => _simulate,
        _ => _call,
    };

    /// <summary>The environments the instance serves, which its parts are counted under.</summary>
    public IlEvmSurface Surface { get; }

    private IlEvmLegacyFrameRunner(IlEvmSurface surface) => Surface = surface;

    private static IlEvmFrameRunner General => TBlock.IsActive ? IlEvmFrameRunner.Block : IlEvmFrameRunner.Instance;

    /// <summary>The core's offer of a frame part, counted under the instance's surface as <see cref="IlEvmFrameRunner"/> counts its own.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        long[] counts = IlSurfaceCounts.OfThisThread();
        int surface = (int)Surface;
        counts[IlSurfaceCounts.Offered + surface]++;
        int before = vm.OpCodeCount;
        EvmExceptionType result = RunPart(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags);
        if (vm.OpCodeCount != before)
            counts[IlSurfaceCounts.Compiled + surface]++;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType RunPart(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        if (IlEvmDiagnostics.Enabled || Volatile.Read(ref IlEvmSettings.LegacyDetour))
            return Detour(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags);

        IlEvm.IlContract? contract;
        bool count = false;
        if (TBlock.IsActive && !IlEvmSettings.BlockRuntimeCompile)
            contract = IlEvm.FindPrecompiledOnly(codeInfo, bodies);
        else
            contract = IlEvm.Find(codeInfo, bodies, out _, out count);
        if (contract is null)
            return count ? DeclinedReportingOpcodes : Declined;

        // The start-up window's mark, set once: the read alone stays on the entry.
        if (IlEvmSettings.FirstFrameAt == 0)
            IlEvmSettings.SawFrame();
        bool cancelable = (flags & FrameRunFlags.Cancelable) != 0;
        if (IlEvmSettings.SelfCheck && (!TBlock.IsActive || IlEvmSettings.BlockSelfCheck) &&
            Volatile.Read(ref contract.Check.Verdict) != IlSelfCheck.Compiled)
            return IlEvmFrameRunner.RunUnsettled(codeInfo, contract, vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
        return contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
    }

    /// <summary>
    /// A part the entry does not run itself: declined at once with the kill switch off, as every rules' path but the
    /// Adaptive one declines with no side effect; anything else goes to the general runner.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static EvmExceptionType Detour(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        if (!IlEvmDiagnostics.Enabled && !IlEvmSettings.Enabled && IlEvmSettings.Mode != IlMode.Adaptive)
            return Declined;
        return General.RunGeneral(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override void OnInterpreted(CodeInfo codeInfo, int opCodes)
    {
        if (IlEvmDiagnostics.Enabled || Volatile.Read(ref IlEvmSettings.LegacyDetour))
            General.InterpretedGeneral(codeInfo, opCodes);
        else
            IlEvmFrameRunner.OnInterpretedOther(codeInfo, opCodes);
    }
}
