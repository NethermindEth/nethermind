// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>Appends ABI words to a buffer sized by the codec beforehand.</summary>
internal ref struct AbiWriter(Span<byte> buffer)
{
    private const int Word = AbiWord.Size;

    private readonly Span<byte> _buffer = buffer;

    public int Position { get; private set; }

    public void Write(in ValueHash256 hash) => hash.Bytes.CopyTo(Next());

    public void Write(ulong value) => AbiWord.Write(Next(), value);

    public void Write(bool value) => AbiWord.Write(Next(), value);

    public void Write(Address address) => AbiWord.Write(Next(), address);

    public void Write(in UInt256 value) => AbiWord.Write(Next(), value);

    public void Write(in Int256.Int256 value) => Write((UInt256)value);

    public void WriteOffset(int offset) => Write((ulong)offset);

    /// <summary>Writes <c>bytes</c>: the length word, then the data padded with zeros to whole words.</summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        Write((ulong)data.Length);
        Span<byte> target = _buffer.Slice(Position, AbiWord.PaddedLength(data.Length));
        data.CopyTo(target);
        target[data.Length..].Clear();
        Position += target.Length;
    }

    private Span<byte> Next()
    {
        Span<byte> word = _buffer.Slice(Position, Word);
        Position += Word;
        return word;
    }
}
