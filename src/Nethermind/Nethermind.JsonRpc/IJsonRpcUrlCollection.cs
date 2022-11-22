// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcUrlCollection : IReadOnlyDictionary<int, JsonRpcUrl>
    {
        string[] Urls { get; }
    }
}

