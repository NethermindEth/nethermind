//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.DataMarketplace.Core.Services.Models;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class GasPriceTypesForRpc
    {
        public GasPriceDetailsForRpc? SafeLow { get;  }
        public GasPriceDetailsForRpc? Average { get;  }
        public GasPriceDetailsForRpc? Fast { get;  }
        public GasPriceDetailsForRpc? Fastest { get;  }
        public GasPriceDetailsForRpc? Custom { get;  }
        public string? Type { get;  }
        public ulong UpdatedAt { get;  }
        
        public GasPriceTypesForRpc(GasPriceTypes types)
        {
            SafeLow = new GasPriceDetailsForRpc(types.SafeLow);
            Average = new GasPriceDetailsForRpc(types.Average);
            Fast = new GasPriceDetailsForRpc(types.Fast);
            Fastest = new GasPriceDetailsForRpc(types.Fastest);
            Custom = new GasPriceDetailsForRpc(types.Custom);
            Type = types.Type;
            UpdatedAt = types.UpdatedAt;
        }
    }
}