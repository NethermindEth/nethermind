// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;

namespace Nethermind.Consensus
{
    public interface ISignerStore
    {
        void SetSigner(PrivateKey key);

        void SetSigner(ProtectedPrivateKey key);
    }
}
