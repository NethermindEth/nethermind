// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Evm.CodeAnalysis;

public sealed class CodeInfo(ReadOnlyMemory<byte> code) : ICodeInfo, IThreadPoolWorkItem
{
    private static readonly JumpDestinationAnalyzer _emptyAnalyzer = new(Array.Empty<byte>());
    public static CodeInfo Empty { get; } = new(ReadOnlyMemory<byte>.Empty);
    public ReadOnlyMemory<byte> Code { get; } = code;
    ReadOnlySpan<byte> ICodeInfo.CodeSpan => Code.Span;

    private readonly JumpDestinationAnalyzer _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);

    public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer);

    public bool ValidateJump(int destination) => _analyzer.ValidateJump(destination);

    void IThreadPoolWorkItem.Execute() => _analyzer.Execute();

    public void AnalyzeInBackgroundIfRequired()
    {
        if (!ReferenceEquals(_analyzer, _emptyAnalyzer) && _analyzer.RequiresAnalysis)
        {
#if ZKVM
            _analyzer.Execute();
#else
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
#endif
        }
    }
}
