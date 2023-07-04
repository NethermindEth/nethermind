// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;

namespace Nethermind.Wallet
{
    public interface INodeKeyManager
    {
        ProtectedPrivateKey LoadNodeKey();
        ProtectedPrivateKey LoadSignerKey();
    }
}
