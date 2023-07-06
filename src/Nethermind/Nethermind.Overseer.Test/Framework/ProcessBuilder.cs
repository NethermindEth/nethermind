// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            var process = new Process { EnableRaisingEvents = true };
            process.ErrorDataReceived += ProcessOnErrorDataReceived;
            process.OutputDataReceived += ProcessOnOutputDataReceived;
            process.Exited += ProcessOnExited;
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.FileName = "dotnet";
            var arguments = $"nethermind.dll --config {config} --JsonRpc.Port {httpPort} --Network.P2PPort {p2pPort} --Network.DiscoveryPort {p2pPort} --KeyStore.TestNodeKey {nodeKey}";
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

            return new NethermindProcessWrapper(name, process, httpPort, new PrivateKey(nodeKey).Address, $"enode://{new PrivateKey(nodeKey).PublicKey.ToString(false)}@127.0.0.1:{p2pPort}", new JsonRpcClient($"http://localhost:{httpPort}"));
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
