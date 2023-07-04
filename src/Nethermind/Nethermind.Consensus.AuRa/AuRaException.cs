// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaException : Exception
    {
        protected AuRaException()
        {
        }

        public AuRaException(string message) : base(message)
        {

        }

        public AuRaException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
