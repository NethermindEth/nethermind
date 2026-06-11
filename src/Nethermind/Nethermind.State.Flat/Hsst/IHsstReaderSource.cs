// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Factory for an <see cref="IHsstByteReader{TPin}"/> over a fixed byte region. Readers are
/// typically ref structs and cannot be cached as fields, so consumers that need to traverse the
/// same region more than once (the persisted-snapshot scanner, the N-way merger) hold a small
/// value-type source and mint a fresh reader per use.
/// </summary>
public interface IHsstReaderSource<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    /// <summary>Materialise a fresh reader over this source's region.</summary>
    TReader CreateReader();
}
