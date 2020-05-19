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
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace Nethermind.Baseline
{
    public class ContractType
    {
        public const string MerkleTreeSHA = "MerkleTreeSHA"; // 0
        public const string MiMC = "MiMC"; // 1
        public const string MerkleTreeMiMC = "MerkleTreeMiMC"; // 2
        public const string MerkleTreeControllerSHA = "MerkleTreeControllerSHA"; // 3
        public const string MerkleTreeControllerMiMC = "MerkleTreeControllerMiMC"; // 4
        
        [JsonProperty("contractName")] 
        public string contractName { get; set; }

        [JsonProperty("bytecode")] 
        public string bytecode { get; set; }

        public List<ContractType> DeserializeJson(string filename)
        {
            var contractObject = JsonConvert.DeserializeObject<List<ContractType>>(File.ReadAllText(filename));
            return contractObject;
        }

        public string GetContractBytecode(string contract)
        {   
            string contractBytecode = null;

            var contractType = DeserializeJson("MerkleTreeContracts.json");

            switch(contract)
            {
                case MerkleTreeSHA:
                    return contractBytecode = contractType[0].bytecode;
                case MiMC:
                    return contractBytecode = contractType[1].bytecode;
                case MerkleTreeMiMC:
                    return contractBytecode = contractType[2].bytecode;
                case MerkleTreeControllerSHA:
                    return contractBytecode = contractType[3].bytecode;
                case MerkleTreeControllerMiMC:
                    return contractBytecode = contractType[4].bytecode;
            }
            
            return contractBytecode;
        }
    }
}