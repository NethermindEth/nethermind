// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp
{
    public class RlpException : Exception
    {
        public RlpException(string message, Exception inner)
            : base(message, inner)
        {
        }

        public RlpException(string message)
            : base(message)
        {
        }
    }
}
