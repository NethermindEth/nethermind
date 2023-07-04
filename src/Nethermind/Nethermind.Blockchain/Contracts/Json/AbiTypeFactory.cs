// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts.Json
{
    public class AbiTypeFactory : IAbiTypeFactory
    {
        private readonly AbiType _abiType;

        public AbiTypeFactory(AbiType abiType)
        {
            _abiType = abiType;
        }
        public AbiType? Create(string abiTypeSignature) => _abiType.Name == abiTypeSignature ? _abiType : null;
    }
}
