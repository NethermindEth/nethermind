// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.CodeAnalysis;

public sealed class CodeInfo(in ValueHash256 codeHash, ReadOnlyMemory<byte> code) : ICodeInfo, IThreadPoolWorkItem
{
    public CodeInfo(ReadOnlyMemory<byte> code) : this(ValueKeccak.Compute(code.Span), code) { }

    private static readonly JumpDestinationAnalyzer _emptyAnalyzer = new(Array.Empty<byte>());
    public static CodeInfo Empty { get; } = new(in Keccak.OfAnEmptyString.ValueHash256, ReadOnlyMemory<byte>.Empty);

    private readonly ValueHash256 _codeHash = codeHash;
    public ref readonly ValueHash256 CodeHash => ref _codeHash;
    public ReadOnlyMemory<byte> MachineCode { get; } = code;

    private readonly JumpDestinationAnalyzer _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);

    public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer);

    public bool ValidateJump(int destination)
    {
        return _analyzer.ValidateJump(destination);
    }

    void IThreadPoolWorkItem.Execute()
    {
        _analyzer.Execute();
    }

    public void AnalyzeInBackgroundIfRequired()
    {
        if (!ReferenceEquals(_analyzer, _emptyAnalyzer) && _analyzer.RequiresAnalysis)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
    }
}
