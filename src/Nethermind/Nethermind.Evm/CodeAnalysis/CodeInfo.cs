// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public class CodeInfo : IThreadPoolWorkItem, IEquatable<CodeInfo>
{
    public static CodeInfo Empty { get; } = new();
    private static readonly JumpDestinationAnalyzer _emptyAnalyzer = new(Empty, skipAnalysis: true);

    private CodeInfo() { }

    protected CodeInfo(int version, ReadOnlyMemory<byte> code)
    {
        Version = version;
        Code = code;
        _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(this, skipAnalysis: true);
    }

    public CodeInfo(ReadOnlyMemory<byte> code)
    {
        Code = code;
        _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(this);
    }

    public CodeInfo(IPrecompile? precompile)
    {
        _analyzer = _emptyAnalyzer;
        Precompile = precompile;
    }

    public ReadOnlyMemory<byte> Code { get; }
    public ReadOnlySpan<byte> CodeSpan => Code.Span;

    public IPrecompile? Precompile { get; }

    private readonly JumpDestinationAnalyzer _analyzer;

    public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer) && Precompile is null;
    public bool IsPrecompile => Precompile is not null;

    public bool ValidateJump(int destination)
        => _analyzer.ValidateJump(destination);

    /// <summary>
    /// Gets the version of the code format. 
    /// The default implementation returns 0, representing a legacy code format or non-EOF code.
    /// </summary>
    public int Version { get; } = 0;

    void IThreadPoolWorkItem.Execute()
        => _analyzer.Execute();

    public void AnalyzeInBackgroundIfRequired()
    {
        if (!ReferenceEquals(_analyzer, _emptyAnalyzer) && _analyzer.RequiresAnalysis)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
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
