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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Timers;

namespace Nethermind.Core
{
    /// <summary>
    /// This class is not following much rules or performance - it is just here to analyze in depth the network behaviour.
    /// </summary>
    public static class NetworkDiagTracer
    {
        public const string NetworkDiagTracerPath = @"network_diag.txt";

        public static bool IsEnabled { get; set; }

        private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _events = new();

        public static void Start()
        {
            Timer timer = new();
            timer.Interval = 60000;
            timer.Elapsed += (_, _) => DumpEvents(); 
            timer.Start();
        }

        private static void DumpEvents()
        {
            StringBuilder stringBuilder = new();

            foreach (KeyValuePair<string, ConcurrentQueue<string>> keyValuePair in _events)
            {
                stringBuilder.AppendLine(keyValuePair.Key);
                foreach (string s in keyValuePair.Value)
                {
                    stringBuilder.AppendLine("  " + s);    
                }
            }
            
            File.WriteAllText(NetworkDiagTracerPath, stringBuilder.ToString());
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private static void Add(IPEndPoint? farAddress, string line)
        {
            _events.AddOrUpdate(farAddress?.Address.MapToIPv4().ToString() ?? "null", ni => new ConcurrentQueue<string>(), (s, list) =>
            {
                list.Enqueue(line);
                return list;
            });
        }

        public static void ReportOutgoingMessage(IPEndPoint nodeInfo, string protocol, string messageCode)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} <<< {protocol} {messageCode}");
        }
        
        public static void ReportIncomingMessage(IPEndPoint nodeInfo, string protocol, string info)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} >>> {protocol} {info}");
        }
        
        public static void ReportConnect(IPEndPoint nodeInfo, string clientId)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} CONNECT {clientId}");
        }
        
        public static void ReportDisconnect(IPEndPoint nodeInfo, string details)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} DISCONNECT {details}");
        }
        
        public static void ReportInterestingEvent(IPEndPoint nodeInfo, string details)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} {details}");
        }
    }
}
