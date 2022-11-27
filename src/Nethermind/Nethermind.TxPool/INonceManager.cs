// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public interface INonceManager
{
    UInt256 ReserveNonce(Address address);
    void ReleaseNonce(Address address, UInt256 nonce);
    bool IsNonceUsed(Address address, UInt256 nonce);
    void SetTransactionHash(Address address, UInt256 nonce, Keccak hash);
}
