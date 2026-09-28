// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Io;

/// <summary>
/// Factory for an <see cref="IByteReader{TPin}"/> over a fixed byte region. Readers are
/// typically ref structs and cannot be cached as fields, so consumers that need to traverse the
/// same region more than once (the persisted-snapshot scanner, the N-way merger) hold a small
/// value-type source and mint a fresh reader per use.
/// </summary>
public interface IByteReaderSource<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IByteReader<TPin>, allows ref struct
{
    TReader CreateReader();
}
