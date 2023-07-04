// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts.Json
{
    public interface IAbiTypeFactory
    {
        AbiType? Create(string abiTypeSignature);
    }
}
