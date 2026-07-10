// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
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
    private CodeInfo(JumpDestinationAnalyzer analyzer)
    {
        _analyzer = analyzer;
        _streamBuildState = StreamBuildUnavailable;
    }

    // Regular contract
    public CodeInfo(ReadOnlyMemory<byte> code)
    {
        Code = code;
        if (code.Length == 0)
        {
            _analyzer = _emptyAnalyzer;
            _streamBuildState = StreamBuildUnavailable;
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
        _streamBuildState = StreamBuildUnavailable;
    }

    protected CodeInfo(IPrecompile precompile, ReadOnlyMemory<byte> code)
    {
        Precompile = precompile;
        Code = code;
        _analyzer = null;
        _streamBuildState = StreamBuildUnavailable;
    }

    public ReadOnlyMemory<byte> Code { get; }
    public ReadOnlySpan<byte> CodeSpan => Code.Span;

    public IPrecompile? Precompile { get; }

    private readonly JumpDestinationAnalyzer? _analyzer;
    private int _streamHits;

    private const int StreamBuildIdle = 0;
    private const int StreamBuildScheduled = 1;
    private const int StreamBuildUnavailable = 2;
    private int _streamBuildState;

    // Key into the shared InstructionStreamCache (set by the repository), so a built stream
    // survives this instance's eviction.
    public ValueHash256 CodeHash { get; set; }

    /// <summary>
    /// Returns the built stream from the shared cache, or <c>null</c> until ready: past
    /// <see cref="StreamInterpreter.BuildThreshold"/> the build is scheduled once on the thread pool and callers keep
    /// getting <c>null</c> until it publishes, so no call blocks. The stream is held only by the shared cache (not on
    /// this instance), so retained stream memory is bounded by that cache alone, not by the CodeInfo cache size.
    /// </summary>
    internal InstructionStream? GetOrBuildStream()
    {
        if (Volatile.Read(ref _streamBuildState) == StreamBuildUnavailable)
            return null;
        if (CodeHash != default && InstructionStreamCache.TryGet(CodeHash, out InstructionStream? cached))
            return cached;
        if (Interlocked.Increment(ref _streamHits) < StreamInterpreter.BuildThreshold)
            return null;

        if (Interlocked.CompareExchange(ref _streamBuildState, StreamBuildScheduled, StreamBuildIdle) == StreamBuildIdle)
            ThreadPool.UnsafeQueueUserWorkItem(new StreamBuilder(this), preferLocal: false);

        return null;
    }

    private void BuildStream()
    {
        InstructionStream? stream = InstructionStream.TryBuild(CodeSpan);
        if (stream is not null && CodeHash != default && stream.RetainedBytes <= StreamInterpreter.MaxStreamRetainedBytes)
        {
            InstructionStreamCache.Set(CodeHash, stream);
        }
        else
        {
            Volatile.Write(ref _streamBuildState, StreamBuildUnavailable);
        }
    }

    private sealed class StreamBuilder(CodeInfo codeInfo) : IThreadPoolWorkItem
    {
        public void Execute() => codeInfo.BuildStream();
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
