// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts.Json
{
    public static class AbiDefinitionParserExtensions
    {
        public static void RegisterAbiTypeFactory(this IAbiDefinitionParser parser, AbiType abiType) =>
            parser.RegisterAbiTypeFactory(new AbiTypeFactory(abiType));
    }
}
