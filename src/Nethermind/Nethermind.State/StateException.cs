// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State
{
    public class StateException : Exception
    {
        public StateException() : base()
        {
        }

        public StateException(string message) : base(message)
        {
        }
    }
}
