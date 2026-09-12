// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo : IThreadPoolWorkItem, IEquatable<CodeInfo>
{
    public static CodeInfo Empty { get; }
    // Empty code sentinel
    private static readonly JumpDestinationAnalyzer _emptyAnalyzer;

    static CodeInfo()
    {
        CodeInfo stub = new(); // allocate without analyzer
        _emptyAnalyzer = new JumpDestinationAnalyzer(stub, skipAnalysis: true);
        Empty = new CodeInfo(_emptyAnalyzer);
    }

    // Empty
    private CodeInfo() { }
    private CodeInfo(JumpDestinationAnalyzer analyzer) => _analyzer = analyzer;

    // Regular contract
    public CodeInfo(ReadOnlyMemory<byte> code)
    {
        Code = code;
        if (code.Length == 0)
        {
            _analyzer = _emptyAnalyzer;
        }
        else
        {
            _analyzer = new JumpDestinationAnalyzer(this);
        }
    }

    // Precompile
    public CodeInfo(IPrecompile? precompile)
    {
        Precompile = precompile;
        _analyzer = null;
    }

    public ReadOnlyMemory<byte> Code { get; }
    public ReadOnlySpan<byte> CodeSpan => Code.Span;

    public IPrecompile? Precompile { get; }

    private readonly JumpDestinationAnalyzer? _analyzer;
    private CodeTemplate? _template;
    public ValueHash256 CodeHash { get; set; }

    /// <summary>The compiler-emitted template this code matches, recognized ahead of execution where possible.</summary>
    /// <remarks>
    /// Normally resolved on the analysis thread before the code first runs; a caller that arrives first
    /// recognizes it inline instead. Racing callers may each recognize the code, which is why the result
    /// is idempotent: the duplicate work is preferred over synchronising a lookup that runs once per
    /// distinct code hash. Which arm a given call takes is therefore timing-dependent, and can be,
    /// because the two produce identical gas, state and output.
    /// </remarks>
    internal CodeTemplate Template => CodeAnalysisFlags.Templates
        ? _template ?? PrepareAnalysis()
        : CodeTemplate.None;

    /// <summary>Recognizes the code now, rather than waiting for the analysis thread to reach it.</summary>
    internal CodeTemplate PrepareAnalysis() => _template ??= CodeTemplate.Recognize(CodeSpan, ValidateJump);

    /// <summary>
    /// Returns <c>true</c> when this instance represents non-executable empty bytecode.
    /// </summary>
    /// <remarks>
    /// Empty code is represented by the shared analyzer sentinel so fast paths can test this without inspecting bytecode.
    /// Constructors that create zero-length executable bytecode must assign the sentinel to preserve that invariant.
    /// </remarks>
    public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer);
    public bool IsPrecompile => Precompile is not null;

    public bool ValidateJump(int destination)
        => _analyzer?.ValidateJump(destination) ?? false;

    /// <summary>The jump-destination bitmap of this code, built on first use.</summary>
    internal long[] JumpDestinationBitmap => _analyzer?.JumpDestinationBitmap ?? JumpDestinationAnalyzer.EmptyBitmap;

    void IThreadPoolWorkItem.Execute()
    {
        _analyzer?.Execute();

        // Recognition costs a pass over the code; running it here keeps that pass off the thread that
        // will execute the code, which is the whole reason this work item exists. Reading the property
        // rather than calling recognition directly keeps the build that compiles templates out of this.
        _ = Template;
    }

    public void AnalyzeInBackgroundIfRequired()
    {
        // Analysis only runs ahead of execution on another processor; the guest folds the queue away.
        if (RuntimeInformation.IsSingleProcessor) return;

        if (!ReferenceEquals(_analyzer, _emptyAnalyzer) && (_analyzer?.RequiresAnalysis ?? false))
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    public override bool Equals(object? obj)
        => Equals(obj as CodeInfo);

    public override int GetHashCode()
    {
        if (IsPrecompile)
            return Precompile?.GetType().GetHashCode() ?? 0;
        return CodeSpan.FastHash();
    }

    public bool Equals(CodeInfo? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (IsPrecompile || other.IsPrecompile)
            return Precompile?.GetType() == other.Precompile?.GetType();
        return CodeSpan.SequenceEqual(other.CodeSpan);
    }
}
