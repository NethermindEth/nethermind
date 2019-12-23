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

using System.Text.Json;

namespace Nethermind.BeaconNode.Containers.Json
{
    public static class JsonSerializerOptionsExtensions
    {
        public static void AddCortexContainerConverters(this JsonSerializerOptions options)
        {
            options.Converters.Add(new JsonConverterBlsPublicKey());
            options.Converters.Add(new JsonConverterBlsSignature());
            options.Converters.Add(new JsonConverterBytes32());
            options.Converters.Add(new JsonConverterCommitteeIndex());
            options.Converters.Add(new JsonConverterDomain());
            options.Converters.Add(new JsonConverterEpoch());
            options.Converters.Add(new JsonConverterForkVersion());
            options.Converters.Add(new JsonConverterGwei());
            options.Converters.Add(new JsonConverterHash32());
            options.Converters.Add(new JsonConverterShard());
            options.Converters.Add(new JsonConverterSlot());
            options.Converters.Add(new JsonConverterValidatorIndex());
        }
    }
}
