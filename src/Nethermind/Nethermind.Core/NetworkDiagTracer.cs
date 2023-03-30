// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Timers;
using Nethermind.Logging;

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
        private static ILogger? _logger;

        public static void Start(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
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

            _events.Clear();

            string contents = stringBuilder.ToString();
            File.WriteAllText(NetworkDiagTracerPath, contents);
            _logger?.Info(contents);
        }

        private static void Add(IPEndPoint? farAddress, string line)
        {
            string address = farAddress?.Address.MapToIPv4().ToString() ?? "null";
            ConcurrentQueue<string> queue = _events.GetOrAdd(address, _ => new ConcurrentQueue<string>());
            queue.Enqueue(line);
        }

        public static void ReportOutgoingMessage(IPEndPoint? nodeInfo, string protocol, string info, int size)
        {
            if (!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} <<< {protocol,7} {size,6} {info}");
        }

        public static void ReportIncomingMessage(IPEndPoint? nodeInfo, string protocol, string info, int size)
        {
            if (!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} >>> {protocol,7} {size,6} {info}");
        }

        public static void ReportConnect(IPEndPoint? nodeInfo, string clientId)
        {
            if (!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} CONNECT {clientId}");
        }

        public static void ReportDisconnect(IPEndPoint? nodeInfo, string details)
        {
            if (!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} DISCONNECT {details}");
        }

        public static void ReportInterestingEvent(IPEndPoint? nodeInfo, string details)
        {
            if (!IsEnabled) return;
            Add(nodeInfo, $"{DateTime.UtcNow:HH:mm:ss.ffffff} {details}");
        }
    }
}
