// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization
{
    public class EthSyncException : Exception
    {
        public EthSyncException(string message) : base(message)
        {
        }

        public EthSyncException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
