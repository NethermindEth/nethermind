// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Sockets;

[StructLayout(LayoutKind.Auto)]
public readonly struct ReceiveResult
{
    public int Read { get; init; }
    private readonly bool _isNotNull;

    public readonly bool IsNull => !_isNotNull;

    public ReceiveResult() => _isNotNull = true;

    public bool EndOfMessage { get; init; }
    public bool Closed { get; init; }
}
