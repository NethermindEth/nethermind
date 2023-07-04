// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.AccountAbstraction.Network;

namespace Nethermind.AccountAbstraction.Data
{
    public static class RlpStreamExtensions
    {
        private static readonly UserOperationDecoder _userOperationDecoder = new();

        public static void Encode(this RlpStream rlpStream, UserOperationWithEntryPoint? value)
        {
            _userOperationDecoder.Encode(rlpStream, value);
        }
    }
}
