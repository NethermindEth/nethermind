using System;
using System.Collections.Generic;
using System.Linq;

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
