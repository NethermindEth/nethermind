// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo : IEquatable<CodeInfo>
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
    private Address? _delegatedAddress;
    internal Address? DelegatedAddress
    {
        get
        {
            if (Code.Length != Eip7702Constants.DelegationHeader.Length + Address.Size)
            {
                return null;
            }

            Address? delegatedAddress = Volatile.Read(ref _delegatedAddress);
            if (delegatedAddress is not null)
            {
                return delegatedAddress;
            }

            if (!ICodeInfoRepository.TryGetDelegatedAddress(Code.Span, out Address? parsedAddress))
            {
                return null;
            }

            return Interlocked.CompareExchange(ref _delegatedAddress, parsedAddress, null) ?? parsedAddress;
        }
    }

    public IPrecompile? Precompile { get; }

    private readonly JumpDestinationAnalyzer? _analyzer;
    /// <summary>The keccak of the code, stamped when a cache stores this instance.</summary>
    /// <remarks>An instance that never passed through <c>ICodeCache.Set</c> — a precompile,
    /// <see cref="Empty"/>, or anything resolved under <c>NoopCodeCache</c> — reports <c>default</c>
    /// rather than its own hash. See <see cref="StampCodeHash"/> for why it cannot be re-pointed.</remarks>
    public ValueHash256 CodeHash { get; private set; }

    /// <summary>Stamps the keccak of this instance's code, as an <c>ICodeCache</c> does on insert.</summary>
    /// <remarks><c>CacheCodeInfoRepository</c>'s last-resolved memo decides which bytecode executes from
    /// this value alone, so a stamp that is not the keccak of <see cref="CodeSpan"/> would silently serve
    /// the wrong contract. Re-stamping the same hash is allowed — two threads may race to cache the same
    /// code — but re-pointing an instance at a different one is not.</remarks>
    /// <exception cref="InvalidOperationException">The instance already carries a different hash.</exception>
    internal void StampCodeHash(in ValueHash256 codeHash)
    {
        if (CodeHash != default && CodeHash != codeHash) ThrowRestamped(in codeHash);
        CodeHash = codeHash;

        [DoesNotReturn, StackTraceHidden]
        void ThrowRestamped(in ValueHash256 attempted)
            => throw new InvalidOperationException($"{nameof(CodeInfo)} carrying {CodeHash} cannot be re-stamped as {attempted}");
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

    /// <summary>The jump-destination bitmap of this code, built on first use.</summary>
    internal long[] JumpDestinationBitmap
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _analyzer?.JumpDestinationBitmap ?? JumpDestinationAnalyzer.EmptyBitmap;
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
