// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test.IO;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

/// <summary>Runs the runner binary built next to the tests and checks how its process ends.</summary>
public partial class RunnerProcessTests
{
    [Test]
    public async Task Version_exits_with_ok()
    {
        using RunnerProcess runner = new("--version");

        Assert.That(await runner.WaitForExitAsync(), Is.EqualTo(ExitCodes.Ok), runner.Output);
    }

    [Test]
    public async Task Missing_configuration_exits_with_general_error()
    {
        using RunnerProcess runner = new("--config", "no-such-configuration");

        Assert.That(await runner.WaitForExitAsync(), Is.EqualTo(ExitCodes.GeneralError), runner.Output);
    }

    [Test]
    [Platform(Exclude = "Win")]
    public async Task Sigterm_during_the_start_waits_for_the_node_to_stop()
    {
        using TempPath dataDirectory = TempPath.GetTempDirectory();
        using RunnerProcess runner = StartIsolatedNode(dataDirectory.Path);

        // Logged in the synchronous part of the start, before System.CommandLine holds the action's task.
        await runner.WaitForOutputAsync("Generating private key for the node");
        runner.Terminate();

        await AssertStoppedWith(runner, ExitCodes.SigTerm);
    }

    [Test]
    [Platform(Exclude = "Win")]
    public async Task Sigterm_stops_the_node_with_its_exit_code()
    {
        using TempPath dataDirectory = TempPath.GetTempDirectory();
        using RunnerProcess runner = StartIsolatedNode(dataDirectory.Path);

        await runner.WaitForOutputAsync("Initialization Completed");
        runner.Terminate();

        await AssertStoppedWith(runner, ExitCodes.SigTerm);
    }

    /// <summary>Starts a node with no peers, no discovery and every listener on the loopback interface.</summary>
    private static RunnerProcess StartIsolatedNode(string dataDirectory)
    {
        string p2pPort = FreePort();

        return new RunnerProcess(
            "--config", "hoodi", "--data-dir", dataDirectory,
            "--Init.DiscoveryEnabled", "false", "--Init.PeerManagerEnabled", "false", "--Network.MaxActivePeers", "0",
            "--Network.LocalIp", "127.0.0.1", "--Network.ExternalIp", "127.0.0.1",
            "--Network.P2PPort", p2pPort, "--Network.DiscoveryPort", p2pPort,
            "--JsonRpc.Enabled", "false", "--JsonRpc.Port", FreePort(), "--JsonRpc.EnginePort", FreePort(),
            "--HealthChecks.Enabled", "false");
    }

    private static async Task AssertStoppedWith(RunnerProcess runner, int expectedExitCode)
    {
        int exitCode = await runner.WaitForExitAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(expectedExitCode), runner.Output);
            Assert.That(runner.Output, Does.Contain("Nethermind is shut down"));
        }
    }

    private static string FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
    }

    private sealed partial class RunnerProcess : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private readonly Process _process;
        private readonly StringBuilder _output = new();

        public RunnerProcess(params string[] args)
        {
            string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "nethermind.exe" : "nethermind");
            Assert.That(File.Exists(executable), $"The runner was not built next to the tests at {executable}");

            _process = new Process
            {
                StartInfo = new ProcessStartInfo(executable, args) { RedirectStandardOutput = true, RedirectStandardError = true }
            };
            _process.OutputDataReceived += OnOutput;
            _process.ErrorDataReceived += OnOutput;
            _process.Start();
            // Both streams are drained as they are written, so neither can fill its buffer and block the runner.
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        /// <summary>The output so far, without the console color codes.</summary>
        public string Output
        {
            get
            {
                lock (_output) return ColorCode().Replace(_output.ToString(), string.Empty);
            }
        }

        public async Task<int> WaitForExitAsync()
        {
            using CancellationTokenSource timeout = new(Timeout);

            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                Assert.Fail($"The runner did not exit within {Timeout}:\n{Output}");
            }

            return _process.ExitCode;
        }

        public async Task WaitForOutputAsync(string text)
        {
            Stopwatch elapsed = Stopwatch.StartNew();

            while (!Output.Contains(text, StringComparison.Ordinal))
            {
                if (_process.HasExited) Assert.Fail($"The runner exited with {_process.ExitCode} before logging '{text}':\n{Output}");

                if (elapsed.Elapsed > Timeout)
                {
                    _process.Kill(entireProcessTree: true);
                    Assert.Fail($"The runner did not log '{text}' within {Timeout}:\n{Output}");
                }

                await Task.Delay(100);
            }
        }

        public void Terminate()
        {
            using Process kill = Process.Start("kill", ["-TERM", _process.Id.ToString(CultureInfo.InvariantCulture)]);
            kill.WaitForExit();

            Assert.That(kill.ExitCode, Is.Zero, "kill -TERM failed");
        }

        public void Dispose()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);

            _process.Dispose();
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;

            lock (_output) _output.AppendLine(e.Data);
        }

        [GeneratedRegex(@"\x1B\[[0-9;]*m")]
        private static partial Regex ColorCode();
    }
}
