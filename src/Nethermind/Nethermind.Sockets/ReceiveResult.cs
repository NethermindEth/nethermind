// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Sockets;

public readonly struct ReceiveResult
{
    private readonly bool _isNotNull;

    public readonly bool IsNull => !_isNotNull;

    public ReceiveResult()
    {
        _isNotNull = true;
    }

    public int Read { get; init; }
    public bool EndOfMessage { get; init; }
    public bool Closed { get; init; }
}
