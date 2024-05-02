// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Wallet;

public enum PrivateKeyName
{
    NodeKey,

    /// <summary>
    /// Key used for signing blocks. Original as its loaded on startup. This can later be changed via RPC in <see cref="Signer"/>.
    /// </summary>
    SignerKey
}
