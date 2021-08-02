using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.WebSockets
{
    public interface ISocketHandler : IDisposable
    {
        Task SendRawAsync(string data);
        Task<ReceiveResult> GetReceiveResult(byte[] buffer);
        Task CloseAsync(ReceiveResult result);
    }
}
