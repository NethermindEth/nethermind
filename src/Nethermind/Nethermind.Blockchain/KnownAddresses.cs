/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

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
        
        public static Dictionary<Address, string> GoerliValidators = new Dictionary<Address, string>
        {
            {new Address("0xa6DD2974B96e959F2c8930024451a30aFEC24203"), "Ethereum Foundation"},
            {new Address("0x000000568b9b5A365eaa767d42e74ED88915C204"), "POA"},
            {new Address("0x631AE5c534fE7b35aaF5243b54e5ac0CFc44E04C"), "Yucong Sun"},
            {new Address("0xD9A5179F091d85051d3C982785Efd1455CEc8699"), "Prysm Labs"},
            {new Address("0xA8e8F14732658E4B51E8711931053a8A69BaF2B1"), "Dapowerplay"},
            {new Address("0x8b24Eb4E6aAe906058242D83e51fB077370c4720"), "Infura"},
            {new Address("0x4c2ae482593505f0163cdeFc073e81c63CdA4107"), "Nethermind"},
            {new Address("0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd"), "Pantheon"},
            {new Address("0xe0a2Bd4258D2768837BAa26A28fE71Dc079f84c7"), "Parity"},
            {new Address("0x9d525e28fe5830ee92d7aa799c4d21590567b595"), "roninkaizen"}
        };
        
        public static Dictionary<Address, string> RinkebyValidators = new Dictionary<Address, string>
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