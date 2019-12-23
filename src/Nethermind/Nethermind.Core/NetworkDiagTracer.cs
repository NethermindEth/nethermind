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
using System.IO;
using System.Runtime.CompilerServices;

namespace Nethermind.Core
{
    public static class NetworkDiagTracer
    {
        public static bool IsEnabled => true;

        static NetworkDiagTracer()
        {
            if (!Directory.Exists("network"))
            {
                Directory.CreateDirectory("network");
            }
        }
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        private static void LogLine(Guid sessionGuid, string line)
        {
            File.AppendAllText(Path.Combine("network", sessionGuid.ToString()), string.Concat(line, Environment.NewLine));
        }
        
        public static void ReportOutgoingMessage(Guid sessionId, string protocol, string messageCode)
        {
            if(!IsEnabled) return;
            LogLine(sessionId, $"{DateTime.UtcNow:HH:mm:ss.ffffff} <<< {protocol} {messageCode}");
        }
        
        public static void ReportIncomingMessage(Guid sessionId, string protocol, string messageCode)
        {
            if(!IsEnabled) return;
            LogLine(sessionId, $"{DateTime.UtcNow:HH:mm:ss.ffffff} >>> {protocol} {messageCode}");
        }
        
        // public static void ReportConnect(Guid sessionId, string clientId)
        // {
        //     if(!IsEnabled) return;
        //     LogLine(sessionId, $"{DateTime.UtcNow:HH:mm:ss.ffffff} CONNECT {clientId}");
        // }
        
        public static void ReportDisconnect(Guid sessionId, string details)
        {
            if(!IsEnabled) return;
            LogLine(sessionId, $"{DateTime.UtcNow:HH:mm:ss.ffffff} DISCONNECT {details}");
        }
        
        public static void ReportInterestingEvent(Guid sessionId, string details)
        {
            if(!IsEnabled) return;
            LogLine(sessionId, $"{DateTime.UtcNow:HH:mm:ss.ffffff} {details}");
        }
    }
}