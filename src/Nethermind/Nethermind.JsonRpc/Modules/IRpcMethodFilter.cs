// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules
{
    internal interface IRpcMethodFilter
    {
        bool AcceptMethod(string methodName);
    }
}
