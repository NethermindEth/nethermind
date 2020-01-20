//  Copyright (c) 2018 Demerzel Solutions Limited
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
        public static bool IsEnabled { get; set; }

        private static ConcurrentDictionary<string, List<string>> events = new ConcurrentDictionary<string, List<string>>();

        public static void Start()
        {
            Timer timer = new Timer();
            timer.Interval = 60000;
            timer.Elapsed += (sender, args) => DumpEvents(); 
            timer.Start();
        }

        private static void DumpEvents()
        {
            StringBuilder stringBuilder = new StringBuilder();
            foreach (KeyValuePair<string,List<string>> keyValuePair in events)
            {
                stringBuilder.AppendLine(keyValuePair.Key);
                foreach (string s in keyValuePair.Value)
                {
                    stringBuilder.AppendLine("  " + s);    
                }
            }
            
            File.WriteAllText(@"network_diag.txt", stringBuilder.ToString());
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private static void Add(string nodeInfo, string line)
        {
            events.AddOrUpdate(nodeInfo, ni => new List<string>(), (s, list) =>
            {
                list.Add(line);
                return list;
            });
        }
        
        public static void ReportOutgoingMessage(string nodeInfo, string protocol, string messageCode)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} <<< {protocol} {messageCode}");
        }
        
        public static void ReportIncomingMessage(string nodeInfo, string protocol, string info)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} >>> {protocol} {info}");
        }
        
        public static void ReportConnect(string nodeInfo, string clientId)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} CONNECT {clientId}");
        }
        
        public static void ReportDisconnect(string nodeInfo, string details)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} DISCONNECT {details}");
        }
        
        public static void ReportInterestingEvent(string nodeInfo, string details)
        {
            if(!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} {details}");
        }
    }
}