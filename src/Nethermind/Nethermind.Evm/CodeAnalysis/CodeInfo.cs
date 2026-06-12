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
    private InstructionStream? _stream;
    private int _streamBuildQueued;
    private volatile bool _streamUnavailable;

    /// <summary>
    /// Returns the preprocessed instruction stream for this code. The first execution queues
    /// the build on the thread pool — it runs in the gaps the IO-bound frames leave on the
    /// cores, never on the executing frame. Returns <c>null</c> while the build is in flight
    /// and permanently when the code cannot be streamed (empty, precompile, oversized) —
    /// callers fall back to the bytecode loop.
    /// </summary>
    /// <remarks>
    /// With <see cref="StreamInterpreter.SynchronousBuild"/> (tests and consensus gates) the
    /// build is eager and inline so single executions engage the stream deterministically.
    /// </remarks>
    public InstructionStream? GetOrBuildStream()
    {
        InstructionStream? stream = Volatile.Read(ref _stream);
        if (stream is not null)
            return stream;
        if (_streamUnavailable || IsEmpty || IsPrecompile)
            return null;

        if (StreamInterpreter.SynchronousBuild)
        {
            BuildAndPublishStream();
            return Volatile.Read(ref _stream);
        }

        if (Interlocked.Exchange(ref _streamBuildQueued, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static codeInfo => codeInfo.BuildAndPublishStream(), this, preferLocal: false);
        }

        return null;
    }

    private void BuildAndPublishStream()
    {
        InstructionStream? stream = InstructionStream.TryBuild(CodeSpan);
        if (stream is null)
        {
            // Ordered after any (here: no) stream write; later callers short-circuit before
            // touching the counter, so unbuildable code is never re-analyzed.
            _streamUnavailable = true;
            return;
        }

        Interlocked.CompareExchange(ref _stream, stream, null);
    }

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
        if (!ReferenceEquals(_analyzer, _emptyAnalyzer) && (_analyzer?.RequiresAnalysis ?? false))
        {
#if ZK_EVM
            _analyzer.Execute();
#else
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
#endif
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
