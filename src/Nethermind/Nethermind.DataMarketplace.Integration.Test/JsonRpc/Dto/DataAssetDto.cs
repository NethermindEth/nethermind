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

namespace Nethermind.DataMarketplace.Integration.Test.JsonRpc.Dto
{
    public class DataAssetDto
    {
        // Default ID when adding a new data asset (otherwise, will fail for null).
        public string Id { get; set; } = "0xd45c6b02474e7c60aeaf60df4ee451a53a09bb5df0a7e9231a0def145785f086"; 
        public string Name { get; set; }
        public string Description { get; set; }
        public string UnitPrice { get; set; }
        public string UnitType { get; set; }
        public string QueryType { get; set; }
        public uint MinUnits { get; set; }
        public uint MaxUnits { get; set; }
        public DataAssetRulesDto Rules { get; set; }
        public DataAssetProviderDto Provider { get; set; }
        public string File { get; set; }
        public byte[] Data { get; set; }
    }
}