// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts.Json
{
    public interface IAbiDefinitionParser
    {
        AbiDefinition Parse(string json, string name = null);
        AbiDefinition Parse(Type type);
        public AbiDefinition Parse<T>() => Parse(typeof(T));
        void RegisterAbiTypeFactory(IAbiTypeFactory abiTypeFactory);
    }
}
