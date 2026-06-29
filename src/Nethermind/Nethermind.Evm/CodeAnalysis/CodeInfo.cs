// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public class CodeInfo : IThreadPoolWorkItem, IEquatable<CodeInfo>
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
        _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(this);
    }

    // Precompile
    public CodeInfo(IPrecompile? precompile)
    {
        Precompile = precompile;
        _analyzer = null;
    }

    protected CodeInfo(IPrecompile precompile, ReadOnlyMemory<byte> code)
    {
        Precompile = precompile;
        Code = code;
        _analyzer = null;
    }

    public ReadOnlyMemory<byte> Code { get; }
    public ReadOnlySpan<byte> CodeSpan => Code.Span;

    public IPrecompile? Precompile { get; }

    private readonly JumpDestinationAnalyzer? _analyzer;

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

    void IThreadPoolWorkItem.Execute()
        => _analyzer?.Execute();

    public void AnalyzeInBackgroundIfRequired()
    {
        // No background pass in the single-threaded zkVM guest; ValidateJump builds the
        // jump bitmap lazily, so code that never jumps skips analysis entirely.
#if !ZK_EVM
        if (!ReferenceEquals(_analyzer, _emptyAnalyzer) && (_analyzer?.RequiresAnalysis ?? false))
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
#endif
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
