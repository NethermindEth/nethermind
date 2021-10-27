//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using Nethermind.Config;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction
{
    public interface IAccountAbstractionConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether UserOperations are allowed.",
            DefaultValue = "false")]
        bool Enabled { get; set; }

        [ConfigItem(
            Description = "Defines the maximum number of UserOperations that can be kept in memory by clients",
            DefaultValue = "200")]
        int UserOperationPoolSize { get; set; }

        [ConfigItem(
            Description =
                "Defines the hex string representation of the address of the EntryPoint contract to which transactions will be made",
            DefaultValue = "")]
        string EntryPointContractAddress { get; set; }

        [ConfigItem(
            Description =
                "Defines the hex string representation of the address of the create2Factory contract which was used to create the entryPoint",
            DefaultValue = "")]
        string Create2FactoryAddress { get; set; }

        [ConfigItem(
            Description = "Defines the minimum gas price for a user operation to be accepted",
            DefaultValue = "1")]
        UInt256 MinimumGasPrice { get; set; }

        [ConfigItem(
            Description = "Defines the string URL for the flashbots bundle reception endpoint",
            DefaultValue = "https://relay.flashbots.net/")]
        string FlashbotsEndpoint { get; set; }
    }
}
