// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;

namespace Nethermind.Network
{
    public class SessionMonitor : ISessionMonitor
    {
        private Timer _pingTimer;

        private readonly INetworkConfig _networkConfig;
        private readonly ILogger _logger;

        private TimeSpan _pingInterval;
        private List<Task<bool>> _pingTasks = new();

        public SessionMonitor(INetworkConfig config, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _networkConfig = config ?? throw new ArgumentNullException(nameof(config));

            _pingInterval = TimeSpan.FromMilliseconds(_networkConfig.P2PPingInterval);
        }

        public void Start()
        {
            StartPingTimer();
        }

        public void Stop()
        {
            StopPingTimer();
        }

        private ConcurrentDictionary<Guid, ISession> _sessions = new();

        public void AddSession(ISession session)
        {
            session.Disconnected += OnDisconnected;
            if (session.State < SessionState.DisconnectingProtocols)
            {
                _sessions.TryAdd(session.SessionId, session);
            }
        }

        private void OnDisconnected(object sender, DisconnectEventArgs e)
        {
            ISession session = (ISession)sender;
            session.Disconnected -= OnDisconnected;
            _sessions.TryRemove(session.SessionId, out session);
        }

        private void SendPingMessages()
        {
            Task task = Task.Run(SendPingMessagesAsync).ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError)
                {
                    if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Error during send ping messages: {x.Exception}");
                }
            });

            task.Wait();
        }

        private async Task SendPingMessagesAsync()
        {
            foreach (ISession session in _sessions.Values)
            {
                if (session.State == SessionState.Initialized && DateTime.UtcNow - session.LastPingUtc > _pingInterval)
                {
                    Task<bool> pingTask = SendPingMessage(session);
                    _pingTasks.Add(pingTask);
                }
            }

            if (_pingTasks.Any())
            {
                bool[] tasks = await Task.WhenAll(_pingTasks);
                int tasksLength = tasks.Length;
                if (tasksLength != 0)
                {
                    int successes = tasks.Count(x => x);
                    int failures = tasksLength - successes;
                    if (_logger.IsTrace) _logger.Trace($"Sent ping messages to {tasksLength} peers. Received {successes} pongs.");
                    if (failures > tasks.Length / 3)
                    {
                        decimal percentage = (decimal)failures / tasksLength;
                        if (_logger.IsInfo) _logger.Info($"{percentage:P0} of nodes did not respond to a Ping message - {failures}/{tasksLength}");
                    }
                }
            }
            else if (_logger.IsTrace) _logger.Trace("Sent no ping messages.");
        }

        private async Task<bool> SendPingMessage(ISession session)
        {
            if (session.PingSender is null)
            {
                /* this would happen when session is initialized already but the protocol is not yet initialized
                   we do not have a separate session state for it at the moment */
                return true;
            }

            if (session.IsClosing)
            {
                return true;
            }

            DateTime pingTime = DateTime.UtcNow;
            if (pingTime - session.LastPingUtc > _pingInterval)
            {
                session.LastPingUtc = pingTime;
                bool pongReceived = await session.PingSender.SendPing();
                if (!pongReceived)
                {
                    if (!session.IsClosing)
                    {
                        if (_logger.IsDebug) _logger.Debug($"No pong received in response to the {pingTime:T} ping at {session?.Node:c} | last pong time {session.LastPongUtc:T}");
                        return false;
                    }

                    return true;
                }

                session.LastPongUtc = DateTime.UtcNow;
                return true;
            }

            return true;
        }

        private void StartPingTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting session monitor");

            _pingTimer = new Timer(_networkConfig.P2PPingInterval) { AutoReset = false };
            _pingTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    _pingTimer.Enabled = false;
                    SendPingMessages();
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Ping timer failed", exception);
                }
                finally
                {
                    _pingTasks.Clear();
                    _pingTimer.Enabled = true;
                }
            };

            _pingTimer.Start();
        }

        private void StopPingTimer()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping session monitor");
                _pingTimer?.Stop();
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Error("DEBUG/ERRUR Error during ping timer stop", e);
            }
        }
    }
}
