using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Sockets
{
    /// <summary>
    /// Interface that provides lower level operations (in comparison to <see cref="ISocketsClient"/>)
    /// from a specific socket implementation like for example WebSockets, UnixDomainSockets or network sockets.
    /// </summary>
    public interface ISocketHandler : IDisposable
    {
        Task SendRawAsync(string data);
        Task<ReceiveResult> GetReceiveResult(byte[] buffer);
        Task CloseAsync(ReceiveResult result);
    }
}
