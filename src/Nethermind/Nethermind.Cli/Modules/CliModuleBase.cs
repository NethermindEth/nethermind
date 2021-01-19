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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Cli.Modules
{
    public abstract class CliModuleBase
    {
        protected ICliEngine Engine { get; }
        protected INodeManager NodeManager { get; }

        protected CliModuleBase(ICliEngine engine, INodeManager nodeManager)
        {
            Engine = engine;
            NodeManager = nodeManager;
        }

        protected static Address CliParseAddress(string addressHex)
        {
            try
            {
                Address address = new Address(addressHex);
                return address;
            }
            catch (Exception)
            {
                if (!addressHex.Contains("0x"))
                {
                    throw new CliArgumentParserException($"Invalid address format \"{addressHex}\". Have you remembered to add '\"\"'? Expected format: \"0x000102030405060708090a0b0c0d0e0f10111213\".");
                }

                throw new CliArgumentParserException($"Invalid address format \"{addressHex}\". Expected format: \"0x000102030405060708090a0b0c0d0e0f10111213\".");
            }
        }

        protected static Keccak CliParseHash(string hashHex)
        {
            try
            {
                Keccak hash = new Keccak(hashHex);
                return hash;
            }
            catch (Exception)
            {
                if (!hashHex.Contains("0x"))
                {
                    throw new CliArgumentParserException($"Invalid hash format \"{hashHex}\". Have you remembered to add '\"\"'? Expected format: \"0x000102030405060708090a0b0c0d00e0f101112131415161718191a1b1c1d1e1f\".");
                }
                
                throw new CliArgumentParserException($"Invalid hash format \"{hashHex}\". Expected format: \"0x000102030405060708090a0b0c0d00e0f101112131415161718191a1b1c1d1e1f\"");
            }
        }
    }
}
