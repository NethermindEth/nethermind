using System;
using Nethermind.Core;

namespace Nethermind.DataMarketplace.Initializers
{
    public interface INdmCapabilityConnector
    {
        void Init(Func<Address> addressToValidate = null);
        void AddCapability();
    }
}