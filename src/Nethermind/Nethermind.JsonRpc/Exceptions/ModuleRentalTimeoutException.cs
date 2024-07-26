// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Exceptions
{
    public class ModuleRentalTimeoutException : TimeoutException
    {
        public ModuleRentalTimeoutException()
        {
        }

        public ModuleRentalTimeoutException(string message)
            : base(message)
        {
        }

        public ModuleRentalTimeoutException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
