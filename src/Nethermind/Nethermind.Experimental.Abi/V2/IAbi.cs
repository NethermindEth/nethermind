// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Experimental.Abi.V2;

// TODO: Use a more efficient `BinaryReader/Writer` that operates on `Span` instead of `Stream`
public class IAbi<T>
{
    public required string Name { get; init; }
    public required Func<BinaryReader, T> Read { get; init; }
    public required Action<BinaryWriter, T> Write { get; init; }

    public override string ToString() => Name;
}
