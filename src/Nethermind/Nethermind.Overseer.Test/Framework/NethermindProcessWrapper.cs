//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
