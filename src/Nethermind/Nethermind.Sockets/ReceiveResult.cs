using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Sockets
{
    public class ReceiveResult
    {
        public int Read { get; set; }
        public bool EndOfMessage { get; set; }
        public bool Closed { get; set; }
        public string CloseStatusDescription { get; set; }
    }
}
