// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Evm.Test.Tracing;

internal static class GethTxTraceEntryExtensions
{
    public static int StackWordCount(this GethTxTraceEntry entry) =>
        entry.Stack is { } s ? s.Length / EvmStack.WordSize : 0;

    public static int MemoryWordCount(this GethTxTraceEntry entry) =>
        entry.Memory is { } m ? m.Length / EvmPooledMemory.WordSize : 0;

    public static UInt256 GetStackWord(this GethTxTraceEntry entry, int index) =>
        new(entry.Stack!.Value.Span.Slice(index * EvmStack.WordSize, EvmStack.WordSize), isBigEndian: true);

    public static UInt256 GetMemoryWord(this GethTxTraceEntry entry, int index) =>
        new(entry.Memory!.Value.Span.Slice(index * EvmPooledMemory.WordSize, EvmPooledMemory.WordSize), isBigEndian: true);
}
