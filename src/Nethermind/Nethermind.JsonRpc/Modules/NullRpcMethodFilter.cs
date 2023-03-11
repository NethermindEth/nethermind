// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules
{
    internal class NullRpcMethodFilter : IRpcMethodFilter
    {
        private NullRpcMethodFilter()
        {
        }

        public static NullRpcMethodFilter Instance { get; } = new();

        public bool AcceptMethod(string methodName)
        {
            return true;
        }
    }
}
