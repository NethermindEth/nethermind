// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Sockets
{
    public class ReceiveResult
    {
        public int Read { get; init; }
        public bool EndOfMessage { get; init; }
        public bool Closed { get; init; }
        public string? CloseStatusDescription { get; init; }
    }
}
