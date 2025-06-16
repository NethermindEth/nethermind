// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Experimental.Abi;

public delegate T AbiReadFunc<out T>(ref BinarySpanReader r);
public delegate void AbiWriteAction<in T>(ref BinarySpanWriter w, T value);
public delegate int AbiSizeFunc<in T>(T value); // TODO: Use `UInt256` when dealing with sizes according to the ABI spec

public class Abi<T>
{
    public required string Name { get; init; }
    public bool IsDynamic { get; init; }
    public required AbiReadFunc<T> Read { get; init; }
    public required AbiWriteAction<T> Write { get; init; }
    public required AbiSizeFunc<T> Size { get; init; }

    public override string ToString() => Name;
}
