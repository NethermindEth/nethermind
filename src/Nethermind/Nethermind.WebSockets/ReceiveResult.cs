using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.WebSockets
{
    public class ReceiveResult
    {
        public int Read { get; set; }
        public bool EndOfMessage { get; set; }
        public bool Closed { get; set; }
        public WebSocketCloseStatus? CloseStatus { get; set; }
        public string CloseStatusDescription { get; set; }
    }
}
