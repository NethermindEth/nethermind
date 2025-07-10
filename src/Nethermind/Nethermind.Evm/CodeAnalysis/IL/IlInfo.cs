using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL.Delegates;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using static Nethermind.Evm.VirtualMachine;

[assembly: InternalsVisibleTo("Nethermind.Evm.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Benchmarks")]

namespace Nethermind.Evm.CodeAnalysis.IL;

public enum ILMode
{
    NO_ILVM = 0b00000001,
    AOT_MODE = 0b00000010,
}

public enum AnalysisPhase
{
    NotStarted, Queued, Processing, Completed, Failed, Skipped
}

/// <summary>
/// Represents the IL-EVM information about the contract.
/// </summary>
/// 
public class IlInfo
{
    /// <summary>
    /// Represents an information about IL-EVM being not able to optimize the given <see cref="CodeInfo"/>.
    /// </summary>
    public static IlInfo Empty() => new();

    public bool IsNotProcessed => AnalysisPhase is AnalysisPhase.NotStarted;
    public bool IsPrecompiled => AnalysisPhase is AnalysisPhase.Completed;


    public AnalysisPhase AnalysisPhase = AnalysisPhase.NotStarted;

    // assumes small number of ILed
    public ILEmittedMethod? PrecompiledContract { get; set; }
}
