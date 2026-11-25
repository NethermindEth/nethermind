// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.TxPool
{
    [Flags]
    public enum TxHandlingOptions
    {
        None = 0,

        /// <summary>
        /// Tries to find the valid nonce for the given account
        /// </summary>
        ManagedNonce = 1,

        /// <summary>
        /// Keeps trying to push the transaction until it is included in a block
        /// </summary>
        PersistentBroadcast = 2,

        /// <summary>
        /// Allows transaction to be signed by node even if its already signed
        /// </summary>
        AllowReplacingSignature = 8,

        All = ManagedNonce | PersistentBroadcast | AllowReplacingSignature
    }
}
