// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Abi
{
    public class AbiException : Exception
    {
        public AbiException(string message) : base(message)
        {
        }

        public AbiException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
