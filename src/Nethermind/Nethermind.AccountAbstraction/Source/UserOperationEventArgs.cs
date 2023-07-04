// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationEventArgs : EventArgs
    {
        public Address EntryPoint { get; }
        public UserOperation UserOperation { get; }

        public UserOperationEventArgs(UserOperation userOperation, Address entryPoint)
        {
            EntryPoint = entryPoint;
            UserOperation = userOperation;
        }
    }
}
