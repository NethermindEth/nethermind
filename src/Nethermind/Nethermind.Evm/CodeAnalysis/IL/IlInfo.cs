using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Logging;
using Nethermind.State;
using static Nethermind.Evm.VirtualMachine;

[assembly: InternalsVisibleTo("Nethermind.Evm.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Benchmarks")]

namespace Nethermind.Evm.CodeAnalysis.IL;

public static class ILMode
{
    public const int NO_ILVM            = 0b00000000;
    public const int FULL_AOT_MODE      = 0b00000010;
}

public enum AnalysisPhase
{
    NotStarted, Queued, Processing, Completed, Failed, Skipped
}

/// <summary>
/// Represents the IL-EVM information about the contract.
/// </summary>
/// 
internal class IlInfo
{
    /// <summary>
    /// Represents an information about IL-EVM being not able to optimize the given <see cref="CodeInfo"/>.
    /// </summary>
    public static IlInfo Empty() => new();

    public bool IsNotProcessed => AnalysisPhase is AnalysisPhase.NotStarted;
    public bool IsProcessed => AnalysisPhase is AnalysisPhase.Completed;


    public AnalysisPhase AnalysisPhase = AnalysisPhase.NotStarted;

    // assumes small number of ILed
    public ILExecutionStep? PrecompiledContract { get; set; }
}
