// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class BlockchainException : Exception
    {
        public BlockchainException(string message) : base(message)
        {
        }

        public BlockchainException(string message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}
