using System;

namespace Nethermind.Grpc.Models
{
    public class NdmQueryDataEventArgs : EventArgs
    {
        public NdmQueryData Data { get; }
        
        public NdmQueryDataEventArgs(NdmQueryData data)
        {
            Data = data;
        }
    }
}