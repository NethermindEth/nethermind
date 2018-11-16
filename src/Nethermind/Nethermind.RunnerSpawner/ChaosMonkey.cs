/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Timers;
using Nethermind.Core.Logging;

namespace Nethermind.RunnerSpawner
{
    public class ChaosMonkey
    {
        private readonly ILogger _logger;
        private readonly ChaosMonkeyOptions _options;

        public class ChaosMonkeyOptions
        {
            public int IntervalSeconds { get; set; }
            public int AllDownIntervalSeconds { get; set; }
            public int RatioOffOn { get; set; } = 3;
        }

        private ProcessWrapper[] _processes;

        public ChaosMonkey(ILogger logger, ChaosMonkeyOptions options, params ProcessWrapper[] processes)
        {
            _logger = logger;
            _options = options;
            _processes = processes;
        }

        private System.Timers.Timer _timer;
        private System.Timers.Timer _allDownTimer;

        public void Stop()
        {
            foreach (ProcessWrapper wrapper in _processes)
            {
                _logger.Info($"Closing {wrapper.Name}...");
                if (!wrapper.Process.HasExited)
                {
                    wrapper.Process.Kill();
                    wrapper.Process.WaitForExit(5000);
                    if (wrapper.Process.HasExited)
                    {
                        _logger.Info($"{wrapper.Name} closed.");
                    }
                    else
                    {
                        _logger.Error($"{wrapper.Name} could not be closed.");
                    }
                }
                else
                {
                    _logger.Info($"{wrapper.Name} already exited.");
                }

                wrapper.Process.Close();
            }
        }

        public void Start()
        {
            for (int i = 0; i < _processes.Length; i++)
            {
                Thread.Sleep(500);
                _processes[i].Start();
            }

            if (_options.IntervalSeconds != 0)
            {
                _timer = new System.Timers.Timer(_options.IntervalSeconds * 1000);
                _timer.Elapsed += TimerOnElapsed;
                _timer.AutoReset = false;
                _timer.Enabled = true;
            }

            if (_options.AllDownIntervalSeconds != 0)
            {
                _allDownTimer = new System.Timers.Timer(_options.AllDownIntervalSeconds * 1000);
                _allDownTimer.Elapsed += AllDownTimerOnElapsed;
                _allDownTimer.AutoReset = false;
                _allDownTimer.Enabled = true;
            }
        }

        private void AllDownTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            lock (_sync)
            {
                _logger.Info($"Chaos Monkey KILL ALL");
                for (int i = 0; i < _processes.Length; i++)
                {
                    if (_processes[i].IsRunning)
                    {
                        _processes[i].Kill();
                    }
                }

                Thread.Sleep(5000);

                for (int i = 0; i < _processes.Length; i++)
                {
                    if (!_processes[i].IsRunning)
                    {
                        _processes[i].Start();
                    }
                }
            }

            _allDownTimer.Enabled = true;
        }

        private static Random _random = new Random();

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            lock (_sync)
            {
                int i = _random.Next(_processes.Length);
                if (_processes.All(p => p.IsRunning))
                {
                    _logger.Info($"Chaos Monkey KILL {_processes[i].Name}");
                    _processes[i].Kill();
                }
                else if(_processes.All(p => !p.IsRunning))
                {
                    _logger.Info($"Chaos Monkey START {_processes[i].Name}");
                    _processes[i].Start();
                }
                
                int offOnRange = _options.RatioOffOn + 1;
                bool isStart = offOnRange != 0;
                
                while (true)
                {
                    i = _random.Next(_processes.Length);
                    if (isStart && !_processes[i].IsRunning)
                    {
                        _logger.Info($"Chaos Monkey START {_processes[i].Name}");
                        _processes[i].Start();
                        break;
                    }

                    if (!isStart && _processes[i].IsRunning)
                    {
                        _logger.Info($"Chaos Monkey KILL {_processes[i].Name}");
                        _processes[i].Kill();
                        break;
                    }
                }
            }

            _timer.Enabled = true;
        }

        private static object _sync = new Object();
    }

    public class ProcessWrapper
    {
        public string Name { get; set; }

        public ProcessWrapper(string name, Process process)
        {
            Process = process;
            Name = name;
        }

        public Process Process { get; set; }

        public bool IsRunning { get; set; }

        public void Start()
        {
            if (IsRunning)
            {
                throw new InvalidOperationException();
            }

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
            IsRunning = false;
        }
    }
}