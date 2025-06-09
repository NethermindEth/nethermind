// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Experimental.Abi.V2;

public delegate T IAbiReadFunc<out T>(ref BinarySpanReader r);
public delegate void IAbiWriteAction<in T>(ref BinarySpanWriter w, T value);

public delegate int IAbiSizeFunc<in T>(T value);

public class IAbi<T>
{
    public required string Name { get; init; }
    public required IAbiReadFunc<T> Read { get; init; }
    public required IAbiWriteAction<T> Write { get; init; }
    public required IAbiSizeFunc<T> Size { get; init; }

    public override string ToString() => Name;
}
