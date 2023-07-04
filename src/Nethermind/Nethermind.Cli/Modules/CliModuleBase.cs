// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
