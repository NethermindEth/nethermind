// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Init.Snapshot.Test;

internal static class TestHttpListener
{
    public static (HttpListener Listener, int Port) Start()
    {
        for (int attempt = 0; ; attempt++)
        {
            HttpListener listener = new();
            int port = Random.Shared.Next(20000, 60000);
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
    }
}
