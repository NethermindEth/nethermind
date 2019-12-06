using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Cortex.BeaconNode.Services
{
    public interface INodeStart
    {
        Task InitializeNodeAsync();
    }
}
