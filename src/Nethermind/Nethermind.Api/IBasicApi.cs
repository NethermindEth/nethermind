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
// 

using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Api
{
    public interface IBasicApi
    {
        DisposableStack DisposeStack { get; }

        IAbiEncoder AbiEncoder { get; }
        ChainSpec? ChainSpec { get; set; }
        IConfigProvider ConfigProvider { get; }
        ICryptoRandom CryptoRandom { get; }
        IEthereumEcdsa? EthereumEcdsa { get; set; }
        IJsonSerializer EthereumJsonSerializer { get; }
        ILogManager LogManager { get; }
        IList<INethermindPlugin> Plugins { get; }
        ISpecProvider? SpecProvider { get; set; }
        ITimestamper Timestamper { get; }
    }
}