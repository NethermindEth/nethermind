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

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public static class KnownAddresses
    {
        public static string GetDescription(Address address)
        {
            if (GoerliValidators.ContainsKey(address))
            {
                return GoerliValidators[address];
            }

            if (RinkebyValidators.ContainsKey(address))
            {
                return RinkebyValidators[address];
            }

            return "?";
        }

        public static Dictionary<Address, string> GoerliValidators = new()
        {
            {new Address("0xa6DD2974B96e959F2c8930024451a30aFEC24203"), "Ethereum/Geth"},
            {new Address("0x000000568b9b5A365eaa767d42e74ED88915C204"), "POA Network"},
            {new Address("0x631AE5c534fE7b35aaF5243b54e5ac0CFc44E04C"), "Yucong Sun"},
            {new Address("0xD9A5179F091d85051d3C982785Efd1455CEc8699"), "Prysmatic Labs"},
            {new Address("0xA8e8F14732658E4B51E8711931053a8A69BaF2B1"), "DAPowerPlay"},
            {new Address("0x8b24Eb4E6aAe906058242D83e51fB077370c4720"), "Infura"},
            {new Address("0x4c2ae482593505f0163cdeFc073e81c63CdA4107"), "Nethermind"},
            {new Address("0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd"), "ConsenSys/Besu"},
            {new Address("0xe0a2Bd4258D2768837BAa26A28fE71Dc079f84c7"), "Goerli/Afri"},
            {new Address("0x9d525e28fe5830ee92d7aa799c4d21590567b595"), "Goerli/Ronin"},
            {new Address("0x73625f59cadc5009cb458b751b3e7b6b48c06f2c"), "Flashbots"}
        };

        public static Dictionary<Address, string> KnownMiners = new()
        {
            {new Address("0x002e08000acbbae2155fab7ac01929564949070d"), "2Miners: SOLO"},
            {new Address("0x005e288d713a5fb3d7c9cf1b43810a98688c7223"), "xnpool"},
            {new Address("0x04668ec2f57cc15c381b461b9fedab5d451c8f7f"), "zhizhu.top"},
            {new Address("0x06b8c5883ec71bc3f4b332081519f23834c8706e"), "Mining Express"},
            {new Address("0x2a5994b501E6A560e727b6C2DE5D856396aaDd38"), "PandaMiner"},
            {new Address("0x2a65aca4d5fc5b5c859090a6c34d164135398226"), "DwarfPool 1"},
            {new Address("0x35f61dfb08ada13eba64bf156b80df3d5b3a738d"), "firepool"},
            {new Address("0x44fd3ab8381cc3d14afa7c4af7fd13cdc65026e1"), "W Pool"},
            {new Address("0x464b0b37db1ee1b5fbe27300acfbf172fd5e4f53"), "FKPool"},
            {new Address("0x4bb96091ee9d802ed039c4d1a5f6216f90f81b01"), "Ethpool 2"},
            {new Address("0x4c549990a7ef3fea8784406c1eecc98bf4211fa5"), "Hiveon Pool"},
            {new Address("0x52bc44d5378309ee2abf1539bf71de1b7d7be3b5"), "Nanopool"},
            {new Address("0x5a0b54d5dc17e0aadc383d2db43b0a0d3e029c4c"), "Spark Pool"},
            {new Address("0x829bd824b016326a401d083b33d092293333a830"), "F2Pool"},
            {new Address("0x9d6d492bD500DA5B33cf95A5d610a73360FcaAa0"), "HuobiMiningPool"},
            {new Address("0xa3c084Ae80a3f03963017669bC696E961d3aE5d5"), "Uleypool"},
            {new Address("0xA7b0536fB02C593b0dfD82bd65aaCBDd19Ae4777"), "Pooling"},
            {new Address("0xb2930b35844a230f00e51431acae96fe543a0347"), "MiningPoolHub"},
            {new Address("0xDA466bF1cE3C69dbeF918817305cF989A6353423"), "MiningPoolHub"},
            {new Address("0xea674fdde714fd979de3edf0f56aa9716b898ec8"), "Ethermine"},
            {new Address("0xeea5b82b61424df8020f5fedd81767f2d0d25bfb"), "BTC.com Pool"}
        };

        public static Dictionary<Address, string> RinkebyValidators = new()
        {
            // Oraclize, AKASHA, Foundation x3, Infura, Augur, Cotton Candy?
            {new Address("0x42eb768f2244c8811c63729a21a3569731535f06"), "?"},
            {new Address("0x6635f83421bf059cd8111f180f0727128685bae4"), "Infura"},
            {new Address("0x7ffc57839b00206d1ad20c69a1981b489f772031"), "?"},
            {new Address("0xb279182d99e65703f0076e4812653aab85fca0f0"), "?"},
            {new Address("0xd6ae8250b8348c94847280928c79fb3b63ca453e"), "?"},
            {new Address("0xfc18cbc391de84dbd87db83b20935d3e89f5dd91"), "?"},
            {new Address("0xdA35deE8EDDeAA556e4c26268463e26FB91ff74f"), "Provable (Oraclize)"},
        };
    }
}
