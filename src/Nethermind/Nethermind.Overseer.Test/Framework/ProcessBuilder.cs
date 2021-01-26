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
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Overseer.Test.JsonRpc;
using NUnit.Framework;

namespace Nethermind.Overseer.Test.Framework
{
    public class ProcessBuilder
    {
        public NethermindProcessWrapper Create(string name, string workingDirectory, string config, string dbPath, int httpPort, int p2pPort, string nodeKey, string bootnode)
        {
            var process = new Process {EnableRaisingEvents = true};
            process.ErrorDataReceived += ProcessOnErrorDataReceived;
            process.OutputDataReceived += ProcessOnOutputDataReceived;
            process.Exited += ProcessOnExited;
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.FileName = "dotnet";
            var arguments = $"Nethermind.Runner.dll --config {config} --JsonRpc.Port {httpPort} --Network.P2PPort {p2pPort} --Network.DiscoveryPort {p2pPort} --KeyStore.TestNodeKey {nodeKey}";
            if (!string.IsNullOrEmpty(dbPath))
            {
                arguments = $"{arguments} --baseDbPath {dbPath}";
            }

            if (!string.IsNullOrEmpty(bootnode))
            {
                arguments = $"{arguments} --Discovery.Bootnodes {bootnode}";
            }
            
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            process.StartInfo.CreateNoWindow = false;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
            
            return new NethermindProcessWrapper(name, process, httpPort, new PrivateKey(nodeKey).Address,  $"enode://{new PrivateKey(nodeKey).PublicKey.ToString(false)}@127.0.0.1:{p2pPort}",new JsonRpcClient($"http://localhost:{httpPort}"));
        }

        private static void ProcessOnExited(object sender, EventArgs eventArgs)
        {
            TestContext.WriteLine($"Process exited: {((Process)sender).StartInfo.Arguments}");
        }

        private static void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
        {
        }

        private static void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
        {
        }
    }
}
