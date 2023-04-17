// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Overseer.Test.JsonRpc;

namespace Nethermind.Overseer.Test.Framework
{
    public class NethermindProcessWrapper
    {
        public string Enode { get; }
        public IJsonRpcClient JsonRpcClient { get; }
        public string Name { get; }
        public Process Process { get; }
        public bool IsRunning { get; private set; }

        public Address Address { get; private set; }

        public int HttpPort { get; private set; }

        public NethermindProcessWrapper(string name, Process process, int httpPort, Address address, string enode, IJsonRpcClient jsonRpcClient)
        {
            HttpPort = httpPort;
            Address = address;
            Enode = enode;
            JsonRpcClient = jsonRpcClient;
            Name = name;
            Process = process;
        }

        public void Start()
        {
            if (IsRunning)
            {
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Starting in {Process.StartInfo.WorkingDirectory}");
            Process.Start();
            IsRunning = true;
        }

        public void Kill()
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException();
            }

            Process.Kill();
            Process.WaitForExit();
            IsRunning = false;
        }
    }
}
