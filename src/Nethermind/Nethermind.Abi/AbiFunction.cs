// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Abi
{
    public class AbiFunction : AbiBytes
    {
        private AbiFunction() : base(24)
        {
        }

        public static AbiFunction Instance = new();

        public override string Name => "function";
    }
}
