// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.P2P.Subprotocols
{
    public class SubprotocolException : Exception
    {
        public SubprotocolException(string message)
            : base(message)
        {
        }
    }
}
