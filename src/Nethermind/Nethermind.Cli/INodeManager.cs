// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Jint.Native;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Cli
{
    public interface INodeManager : IJsonRpcClient
    {
        string? CurrentUri { get; }

        void SwitchUri(Uri uri);

        Task<JsValue> PostJint(string method, params object[] parameters);
    }
}
