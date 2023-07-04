// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Abi
{
    public class AbiParameter
    {
        public string Name { get; set; } = string.Empty;
        public AbiType Type { get; set; } = AbiType.UInt256;
    }
}
