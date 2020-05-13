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

using Nethermind.Config;

namespace Nethermind.Consensus.AuRa.Config
{
    public interface IAuraConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then Nethermind if mining will seal empty blocks.", DefaultValue = "false")]
        bool ForceSealing { get; set; }
        
        [ConfigItem(Description = "If 'true' then you can run Nethermind only private chains. Do not use with existing Parity AuRa chains.", DefaultValue = "false")]
        bool AllowAuRaPrivateChains { get; set; }
        
        [ConfigItem(Description = "If 'true' then when using BlockGasLimitContractTransitions if the contract returns less than 2mln gas, then 2 mln gas is used.", DefaultValue = "false")]
        bool Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract { get; set; }
    }
}