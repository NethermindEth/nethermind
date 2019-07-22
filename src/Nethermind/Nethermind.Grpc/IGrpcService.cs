using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Grpc.Models;

namespace Nethermind.Grpc
{
    public interface IGrpcService
    {
        void SetPlugin(string name, Keccak headerId);
        event EventHandler<NdmQueryDataEventArgs> NdmQueryDataReceived;
        Task SendNdmQueryAsync(Keccak headerId, Keccak depositId, IEnumerable<string> args, uint iterations = 1);
        Task SendNdmDataAsync(Keccak depositId, string data);
    }
}