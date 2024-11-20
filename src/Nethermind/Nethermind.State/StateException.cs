// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State
{
    public class StateException : Exception
    {
        public StateException() : base()
        {
        }

        protected StateException(string message) : base(message)
        {
        }

        public class StateDeleteNotSupported : NotSupportedException
        {
            public StateDeleteNotSupported(string message) : base(message) { }

            public StateDeleteNotSupported() : base("Verkle Trees does not support deletion of data from the tree") { }
        }
    }
}
