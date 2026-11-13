// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DotNetty.Buffers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.LightClient.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NSubstitute;

namespace Nethermind.LightClient.Test;

public class HostTests
{
    private const string ChromeOrigin = "chrome-extension://nkbihfbeogaeaoehlefnkodbefgpgknn";
    private const string FirefoxOrigin = "moz-extension://12345678-1234-1234-1234-123456789abc";

    [Test]
    public void Data_directory_is_writable_without_changing_existing_files([Values] bool exists)
    {
        string root = Path.Combine(Path.GetTempPath(), $"light-client-directory-{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        if (exists)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "existing"), "preserve");
        }
        try
        {
            bool prepared = LightClientHost.TryPrepareDataDirectory(directory, out string? error);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(prepared, Is.True, error);
                Assert.That(Directory.Exists(directory), Is.True);
                Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName), Is.EquivalentTo(exists ? new[] { "existing" } : []));
                if (exists) Assert.That(File.ReadAllText(Path.Combine(directory, "existing")), Is.EqualTo("preserve"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void Data_directory_reports_file_collision_with_permissions_guidance()
    {
        string file = Path.GetTempFileName();
        try
        {
            bool prepared = LightClientHost.TryPrepareDataDirectory(file, out string? error);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(prepared, Is.False);
                Assert.That(error, Does.Contain(file).And.Contain(Environment.UserName).And.Contain("permissions").And.Contain("1654"));
                Assert.That(File.Exists(file), Is.True);
            }
        }
        finally { File.Delete(file); }
    }

    [Test]
    public void Json_scratch_reservations_do_not_increase_the_committed_response_limit([Values(16, 256, 1024)] int limit)
    {
        using BoundedJsonBuffer buffer = new(limit, 6 * limit);
        buffer.GetSpan(6 * limit)[..limit].Fill(0x5a);
        buffer.Advance(limit);

        RpcException? exception = Assert.Throws<RpcException>(() => buffer.Advance(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Code, Is.EqualTo(-32005));
            Assert.That(buffer.WrittenMemory.ToArray(), Has.Length.EqualTo(limit).And.All.EqualTo(0x5a));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Network_buffers_bound_idle_pools_without_limiting_message_size(CancellationToken token)
    {
        const string ChildProcess = "NETHERMIND_LIGHTCLIENT_BUFFER_TEST";
        if (Environment.GetEnvironmentVariable(ChildProcess) == "1")
        {
            LightClientHost.ConfigureNetworkBuffers();
            AssertNetworkBuffers();
            return;
        }

        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(HostTests).Assembly.Location);
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add($"FullyQualifiedName~{nameof(Network_buffers_bound_idle_pools_without_limiting_message_size)}");
        start.Environment[ChildProcess] = "1";
        start.Environment["io.netty.allocator.maxOrder"] = "11";
        start.Environment["io.netty.allocator.numHeapArenas"] = "8";
        start.Environment["io.netty.allocator.numDirectArenas"] = "8";
        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(token);
        Task<string> error = child.StandardError.ReadToEndAsync(token);
        try { await child.WaitForExitAsync(token); }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }
        Assert.That(child.ExitCode, Is.Zero, $"{await output}\n{await error}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertNetworkBuffers()
    {
        PooledByteBufferAllocator allocator = PooledByteBufferAllocator.Default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NethermindBuffers.Default, Is.SameAs(allocator));
            Assert.That(NethermindBuffers.RlpxAllocator, Is.SameAs(allocator));
            Assert.That(NethermindBuffers.DiscoveryAllocator, Is.SameAs(allocator));
            Assert.That(allocator.Metric.HeapArenas(), Has.Count.EqualTo(2));
            Assert.That(allocator.Metric.DirectArenas(), Has.Count.EqualTo(2));
            Assert.That(allocator.Metric.ChunkSize, Is.EqualTo(2 * 1024 * 1024));
        }

        long HugeAllocations() => allocator.Metric.HeapArenas().Sum(static arena => arena.NumActiveHugeAllocations)
            + allocator.Metric.DirectArenas().Sum(static arena => arena.NumActiveHugeAllocations);
        long hugeBefore = HugeAllocations();
        int[] lengths = [32, allocator.Metric.ChunkSize - 1, allocator.Metric.ChunkSize + 1, 16 * 1024 * 1024];
        IByteBuffer?[] buffers = new IByteBuffer[lengths.Length];
        try
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                IByteBuffer buffer = allocator.Buffer(lengths[i], lengths[i]);
                buffers[i] = buffer;
                buffer.WriteZero(lengths[i]);
                buffer.SetInt(0, 0x534e4150 + i);
                buffer.SetByte(lengths[i] - 1, 0x80 + i);
            }
            using (Assert.EnterMultipleScope())
            {
                for (int i = 0; i < buffers.Length; i++)
                {
                    Assert.That(buffers[i]!.ReadableBytes, Is.EqualTo(lengths[i]));
                    Assert.That(buffers[i]!.GetInt(0), Is.EqualTo(0x534e4150 + i));
                    Assert.That(buffers[i]!.GetByte(lengths[i] - 1), Is.EqualTo(0x80 + i));
                }
                Assert.That(HugeAllocations(), Is.EqualTo(hugeBefore + 2));
            }
        }
        finally
        {
            foreach (IByteBuffer? buffer in buffers) buffer?.Release();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buffers, Has.All.Property(nameof(IByteBuffer.ReferenceCount)).EqualTo(0));
            Assert.That(HugeAllocations(), Is.EqualTo(hugeBefore));
        }
    }

    [Test]
    public void P2p_ports_preserve_defaults()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Assert.That(LightClientHost.ReadPorts(configuration), Is.EqualTo((9050, 9051, 30307)));
    }

    [Test]
    public void P2p_ports_accept_command_line_overrides([Values(0, 29051)] int quicPort)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([
            "--beacon-port", "29050", "--beacon-quic-port", quicPort.ToString(), "--execution-port", "30317",
        ]).Build();

        Assert.That(LightClientHost.ReadPorts(configuration), Is.EqualTo((29050, quicPort, 30317)));
    }

    [Test]
    public void P2p_ports_accept_valid_boundaries(
        [Values("beacon-port", "beacon-quic-port", "execution-port")] string option, [Values(1, 65535)] int port)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", port.ToString()]).Build();

        (int beacon, int quic, int execution) = LightClientHost.ReadPorts(configuration);

        Assert.That(option switch { "beacon-port" => beacon, "beacon-quic-port" => quic, _ => execution }, Is.EqualTo(port));
    }

    [Test]
    public void P2p_ports_reject_out_of_range_values(
        [Values("beacon-port", "beacon-quic-port", "execution-port")] string option, [Values(-1, 65536)] int port)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", port.ToString()]).Build();

        Assert.That(() => LightClientHost.ReadPorts(configuration), Throws.TypeOf<ArgumentOutOfRangeException>()
            .With.Property(nameof(ArgumentOutOfRangeException.ParamName)).EqualTo(option));
    }

    [Test]
    public void P2p_ports_reject_zero_tcp_ports([Values("beacon-port", "execution-port")] string option)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", "0"]).Build();

        Assert.That(() => LightClientHost.ReadPorts(configuration), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase("beacon-port", "30307")]
    [TestCase("beacon-quic-port", "9050")]
    [TestCase("beacon-quic-port", "30307")]
    public void P2p_ports_reject_overlapping_listeners(string option, string port)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", port]).Build();

        Assert.That(() => LightClientHost.ReadPorts(configuration), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void P2p_ports_reject_non_integer_values([Values("beacon-port", "beacon-quic-port", "execution-port")] string option)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine([$"--{option}", "not-a-port"]).Build();

        Assert.That(() => LightClientHost.ReadPorts(configuration), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Gloas_checkpoint_does_not_initialize_rpc_before_the_execution_header_is_authenticated()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type program = typeof(LightClientHost).Assembly.EntryPoint!.DeclaringType!;
        (Type Type, MethodInfo Method) initializer = program.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(flags).Where(method => method.Name.Contains("g__InitializeRpc", StringComparison.Ordinal)),
                (type, method) => (type, method)).Single();
        object closure = Activator.CreateInstance(initializer.Type, nonPublic: true)!;
        Nethermind.BeaconChain.Types.LightClientHeader header = ConsensusTests.Bootstrap(32, 1).Header!;
        header.ExecutionBlockHash = header.Execution!.BlockHash;
        header.Execution = null;
        header.IsGloas = true;
        Consensus.ConsensusPublisher publisher = new(() => header, () => header,
            static (_, _) => throw new AssertionException("Execution resolution has not started."),
            static (_, _) => throw new AssertionException("No execution head has been authenticated."), TimeProvider.System);
        initializer.Type.GetFields(flags).Single(field => field.FieldType == typeof(Consensus.ConsensusPublisher)).SetValue(closure, publisher);

        Assert.That(() => initializer.Method.Invoke(closure, null), Throws.Nothing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(publisher.Heads.Finalized, Is.Null);
            Assert.That(initializer.Type.GetField("rpcInitialized", flags)!.GetValue(closure), Is.False);
        }
    }

    [Test]
    public void Program_interrupt_callback_cancels_startup_and_tolerates_late_dispatch([Values] bool disposed)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type program = typeof(LightClientHost).Assembly.EntryPoint!.DeclaringType!;
        (Type Type, MethodInfo Method) handler = program.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(flags).Where(method => method.ReturnType == typeof(void)
                && method.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(PosixSignalContext)
                && type.GetFields(flags).Any(field => field.FieldType == typeof(CancellationTokenSource))),
                (type, method) => (type, method)).Single();
        object closure = Activator.CreateInstance(handler.Type, nonPublic: true)!;
        using CancellationTokenSource shutdown = new();
        handler.Type.GetFields(flags).Single(field => field.FieldType == typeof(CancellationTokenSource)).SetValue(closure, shutdown);
        PosixSignalContext context = (PosixSignalContext)typeof(PosixSignalContext)
            .GetConstructor(flags, binder: null, [typeof(PosixSignal)], modifiers: null)!.Invoke([PosixSignal.SIGINT]);
        if (disposed) shutdown.Dispose();

        Assert.That(() => handler.Method.Invoke(closure, [context]), Throws.Nothing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Cancel, Is.True);
            Assert.That(shutdown.IsCancellationRequested, Is.EqualTo(!disposed));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Console_signal_cancels_checkpoint_fetch_while_the_status_listener_is_running()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "NethermindLightClientShutdownTests", Guid.NewGuid().ToString("N"));
        using TcpListener provider = new(IPAddress.Loopback, 0);
        provider.Start();
        int port = ((IPEndPoint)provider.LocalEndpoint).Port;
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        string rpcUrl = $"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}";
        reservation.Stop();
        string assembly = typeof(LightClientHost).Assembly.Location;
        string application = Path.GetDirectoryName(assembly)!;
        ProcessStartInfo start = new("dotnet") { UseShellExecute = OperatingSystem.IsWindows() };
        if (OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
        foreach (string argument in new[]
        {
            "exec", "--runtimeconfig", Path.Combine(application, "Nethermind.LightClient.runtimeconfig.json"),
            "--depsfile", Path.Combine(application, "Nethermind.LightClient.deps.json"), assembly,
            "--checkpoint-url", $"https://127.0.0.1:{port}", "--urls", rpcUrl, "--data-dir", directory,
        }) start.ArgumentList.Add(argument);
        using Process client = Process.Start(start)!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            using TcpClient connection = await provider.AcceptTcpClientAsync(timeout.Token);
            using HttpClient rpc = new();
            using JsonDocument progress = JsonDocument.Parse(await rpc.GetStringAsync(rpcUrl + "/status", timeout.Token));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(progress.RootElement.GetProperty("startupStage").GetString(), Is.EqualTo("checkpoint"));
                Assert.That(progress.RootElement.GetProperty("rpcInitialized").GetBoolean(), Is.False);
                Assert.That(progress.RootElement.GetProperty("latest").ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
            using CancellationTokenSource shutdownDeadline = new(TimeSpan.FromSeconds(15));
            await SendInterruptAsync(client.Id, shutdownDeadline.Token);
            try { await client.WaitForExitAsync(shutdownDeadline.Token); }
            catch (OperationCanceledException) when (shutdownDeadline.IsCancellationRequested)
            {
                Assert.Fail($"Console shutdown did not complete for child {client.Id}; exited={client.HasExited}, RPC={rpcUrl}.");
            }
            Assert.That(client.ExitCode, Is.Zero, "The console signal during checkpoint selection must unwind normally.");
        }
        finally
        {
            if (!client.HasExited) client.Kill(entireProcessTree: true);
            await client.WaitForExitAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Malformed_envelope_strings_return_invalid_request_without_aborting_a_batch(
        [Values("jsonrpc", "method", "id")] string field, [Values] bool invalidUtf8, [Values] bool batch)
    {
        string request = field switch
        {
            "jsonrpc" => "{\"jsonrpc\":\"INVALID\",\"method\":\"eth_chainId\",\"id\":1}",
            "id" => "{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":\"INVALID\"}",
            _ => "{\"jsonrpc\":\"2.0\",\"method\":\"INVALID\",\"id\":1}",
        };
        string[] parts = request.Split("INVALID");
        byte[] invalid = invalidUtf8 ? [0xff] : Encoding.UTF8.GetBytes("\\ud800");
        byte[] payload = [.. Encoding.UTF8.GetBytes(parts[0]), .. invalid, .. Encoding.UTF8.GetBytes(parts[1])];
        if (batch)
            payload = [.. Encoding.UTF8.GetBytes("[{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"eth_chainId\"},"),
                .. payload, (byte)']'];
        int invoked = 0;

        (int status, JsonElement response) = await InvokeHttpAsync(payload, (_, _, _) =>
        {
            invoked++;
            return Task.FromResult<object>("0x1");
        });

        JsonElement error = batch ? response[1] : response;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(error.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
            Assert.That(error.GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(invoked, Is.EqualTo(batch ? 1 : 0));
            if (batch) Assert.That(response[0].GetProperty("result").GetString(), Is.EqualTo("0x1"));
        }
    }

    [Test]
    public async Task Malformed_large_method_strings_are_rejected_before_invocation([Values("\\ud800", "\\ud800A")] string method)
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_" + method
            + "\",\"params\":[\"" + new string('a', 65_536) + "\"]}");
        int calls = 0;

        (int status, _) = await InvokeHttpAsync(payload, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<object>(true);
        });

        Assert.That(status, Is.EqualTo(StatusCodes.Status413PayloadTooLarge));
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task Malformed_parameter_strings_are_invalid_parameters_without_peer_warnings(
        [Values("eth_getBalance", "address", "topics")] string parameter, [Values] bool invalidUtf8)
    {
        string method = parameter == "eth_getBalance" ? parameter : "eth_getLogs";
        string parameters = parameter switch
        {
            "eth_getBalance" => "[\"INVALID\",\"latest\"]",
            "address" => "[{\"address\":\"INVALID\"}]",
            _ => "[{\"topics\":[\"INVALID\"]}]",
        };
        string[] parts = ("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method + "\",\"params\":" + parameters + "}").Split("INVALID");
        byte[] invalid = invalidUtf8 ? [0xff] : Encoding.UTF8.GetBytes("\\ud800");
        byte[] payload = [.. Encoding.UTF8.GetBytes(parts[0]), .. invalid, .. Encoding.UTF8.GetBytes(parts[1])];
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Parameters must be checked before fetching a head."), 1,
            specProvider: MainnetSpecProvider.Instance);
        ILogger logger = Substitute.For<ILogger>();

        (int status, JsonElement response) = await InvokeHttpAsync(payload, rpc.InvokeAsync, logger);

        Assert.That(status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(response.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32602));
        Assert.That(logger.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning), Is.False);
    }

    [Test]
    public async Task Authenticated_data_unavailability_is_a_quiet_retryable_rpc_error()
    {
        ILogger logger = Substitute.For<ILogger>();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 1, method = "eth_getBalance" });

        (int status, JsonElement response) = await InvokeHttpAsync(payload,
            static (_, _, _) => Task.FromException<object>(new ExecutionDataUnavailableException("The pinned root is not retained.")), logger);

        Assert.That(status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(response.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32001));
        Assert.That(logger.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning), Is.False);
    }

    [Test]
    public async Task A_large_successful_batch_keeps_a_bounded_error_tail()
    {
        object[] requests = [new { jsonrpc = "2.0", id = 1, method = "eth_chainId" },
            new { jsonrpc = "2.0", id = 2, method = "eth_chainId" }, .. Enumerable.Repeat<object>(0, 10_000)];
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(requests);
        JsonElement result = JsonSerializer.SerializeToElement(Enumerable.Repeat(new string('x', 4096), 1977).ToArray());
        int calls = 0;

        (int status, JsonElement response) = await InvokeHttpAsync(payload, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<object>(result);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(response[0].GetProperty("result").GetArrayLength(), Is.EqualTo(1977));
            Assert.That(response[1].GetProperty("result").GetArrayLength(), Is.EqualTo(1977));
            Assert.That(response.GetArrayLength(), Is.LessThan(requests.Length));
            Assert.That(response[response.GetArrayLength() - 1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            Assert.That(Encoding.UTF8.GetByteCount(response.GetRawText()), Is.LessThanOrEqualTo(RpcEndpoint.BatchResponseLimit));
        }
    }

    [Test]
    public async Task Large_single_call_and_submission_requests_reach_their_handler(
        [Values("eth_call", "eth_estimateGas", "eth_sendRawTransaction")] string method)
    {
        string data = "0x" + new string('0', 49_152 * 2);
        object[] parameters = method == "eth_sendRawTransaction" ? [data] : [new { data }];
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
        int calls = 0;

        (int status, JsonElement response) = await InvokeHttpAsync(payload, (requested, _, _) =>
        {
            Assert.That(requested, Is.EqualTo(method));
            calls++;
            return Task.FromResult<object>("0x1");
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(response.GetProperty("result").GetString(), Is.EqualTo("0x1"));
            Assert.That(calls, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Batch_items_above_the_execution_cap_keep_their_response_ids([Values(33, 100)] int count)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, count)
            .Select(id => new { jsonrpc = "2.0", id, method = "eth_chainId" }));
        int calls = 0;

        (int status, JsonElement response) = await InvokeHttpAsync(payload, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<object>("0x1");
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(calls, Is.EqualTo(RpcEndpoint.MaxBatchCount));
            Assert.That(response.GetArrayLength(), Is.EqualTo(count));
            for (int i = 0; i < count; i++)
            {
                Assert.That(response[i].GetProperty("id").GetInt32(), Is.EqualTo(i));
                if (i < RpcEndpoint.MaxBatchCount) Assert.That(response[i].GetProperty("result").GetString(), Is.EqualTo("0x1"));
                else Assert.That(response[i].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            }
        }
    }

    [Test]
    public async Task Large_scalar_batches_have_bounded_response_and_logging_work()
    {
        ILogger logger = Substitute.For<ILogger>();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Repeat(0, 32_000));
        int calls = 0;

        (int status, JsonElement response) = await InvokeHttpAsync(payload, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<object>("0x1");
        }, logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(calls, Is.Zero);
            Assert.That(response.GetArrayLength(), Is.LessThanOrEqualTo(129));
            Assert.That(response[response.GetArrayLength() - 1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            Assert.That(logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log)), Is.LessThanOrEqualTo(33));
        }
    }

    [Test]
    public async Task Large_notification_batches_remain_response_free([Values] bool trailingRequest)
    {
        object[] requests = [.. Enumerable.Repeat<object>(new { jsonrpc = "2.0", method = "eth_chainId" }, 200)];
        if (trailingRequest) requests[^1] = new { jsonrpc = "2.0", id = 999, method = "eth_chainId" };
        int calls = 0;

        (int status, JsonElement response) = await InvokeHttpAsync(JsonSerializer.SerializeToUtf8Bytes(requests), (_, _, _) =>
        {
            calls++;
            return Task.FromResult<object>("0x1");
        });

        Assert.That(calls, Is.EqualTo(32));
        Assert.That(status, Is.EqualTo(trailingRequest ? StatusCodes.Status200OK : StatusCodes.Status204NoContent));
        if (trailingRequest)
        {
            Assert.That(response.GetArrayLength(), Is.EqualTo(1));
            Assert.That(response[0].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
        }
    }

    [Test]
    public async Task Disposed_rpc_dependencies_are_logged_as_server_faults()
    {
        ILogger logger = Substitute.For<ILogger>();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 1, method = "eth_getBalance" });

        (_, JsonElement response) = await InvokeHttpAsync(payload,
            static (_, _, _) => Task.FromException<object>(new ObjectDisposedException("execution")), logger);

        Assert.That(response.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32000));
        Assert.That(logger.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning), Is.True);
    }

    private static async Task<(int, JsonElement)> InvokeHttpAsync(byte[] request,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger? logger = null)
    {
        DefaultHttpContext context = new();
        using MemoryStream input = new(request);
        using MemoryStream output = new();
        context.Request.Body = input;
        context.Response.Body = output;
        await RpcEndpoint.HandleAsync(context, invoke, logger ?? NullLogger.Instance);
        if (output.Length == 0) return (context.Response.StatusCode, default);
        using JsonDocument response = JsonDocument.Parse(output.ToArray());
        return (context.Response.StatusCode, response.RootElement.Clone());
    }

    [TestCase(null, null, 200)]
    [TestCase("local", null, 200)]
    [TestCase("https://wallet.example", null, 403)]
    [TestCase(null, "attacker.example", 403)]
    [TestCase("http://attacker.example", "attacker.example", 403)]
    [TestCase(ChromeOrigin, null, 200)]
    [TestCase(FirefoxOrigin, null, 200)]
    [TestCase(ChromeOrigin, "attacker.example", 403)]
    [TestCase("null", null, 403)]
    [TestCase("chrome-extension://abc", null, 403)]
    [TestCase("chrome-extension://nkbihfbeogaeaoehlefnkodbefgpgknz", null, 403)]
    [TestCase("https://nkbihfbeogaeaoehlefnkodbefgpgknn", null, 403)]
    [TestCase(ChromeOrigin + ".attacker.example", null, 403)]
    [TestCase(ChromeOrigin + ":8545", null, 403)]
    [TestCase("chrome-extension://user@nkbihfbeogaeaoehlefnkodbefgpgknn", null, 403)]
    [TestCase(ChromeOrigin + "/", null, 403)]
    [TestCase(ChromeOrigin + "?query", null, 403)]
    [TestCase(ChromeOrigin + "#fragment", null, 403)]
    [TestCase("moz-extension://not-a-uuid", null, 403)]
    [TestCase("moz-extension://12345678123412341234123456789abc", null, 403)]
    [TestCase(ChromeOrigin + ", " + FirefoxOrigin, null, 403)]
    [TestCase("http://attacker.example", "attacker.example", 403, "http://127.0.0.1:0;")]
    [TestCase("http://attacker.example", "attacker.example", 403, ";http://127.0.0.1:0")]
    [TestCase("http://attacker.example", "attacker.example", 403, ";;http://127.0.0.1:0;;")]
    [TestCase("http://attacker.example", "attacker.example", 403, "")]
    [TestCase("http://attacker.example", "attacker.example", 403, ";;")]
    [TestCase("http://attacker.example", "attacker.example", 200, "", "8545")]
    [TestCase("http://attacker.example", "attacker.example", 200, "", null, "8545")]
    [TestCase("http://attacker.example", "attacker.example", 200, "", " ; 8545 ; ")]
    [TestCase("http://attacker.example", "attacker.example", 200, "", null, " ; 8545 ; ")]
    [TestCase("http://attacker.example", "attacker.example", 403, "", " ; ")]
    [TestCase("http://attacker.example", "attacker.example", 403, "", null, " ; ")]
    [TestCase("http://attacker.example", "attacker.example", 403, ";;", "8545")]
    [TestCase("http://attacker.example", "attacker.example", 403, ";;", null, "8545")]
    [TestCase(null, null, 200, ";;http://127.0.0.1:0;;")]
    [TestCase("http://wallet.localhost", "wallet.localhost", 200)]
    [TestCase("http://attacker.example", "attacker.example", 403, "http://::1:0")]
    public async Task Rpc_origin_and_host_policy_rejects_cross_site_and_rebinding_requests(
        string? requestOrigin, string? requestHost, int expectedStatus, string rpcUrls = "http://127.0.0.1:0",
        string? httpPorts = null, string? httpsPorts = null)
    {
        await using WebApplication app = CreateApp(rpcUrls.Trim(';').Length == 0 ? "http://127.0.0.1:0" : rpcUrls);
        app.Configuration[WebHostDefaults.HttpPortsKey] = httpPorts;
        app.Configuration[WebHostDefaults.HttpsPortsKey] = httpsPorts;
        LightClientHost.ConfigureRequestPolicy(app, rpcUrls);
        app.MapGet("/", () => "ok");
        app.MapGet("/ws", () => "ok");
        app.MapGet("/status", () => "ok");
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        app.MapGet("/metrics", metrics.WriteAsync);
        await app.StartAsync();
        using HttpClient client = new();
        foreach (string path in new[] { "/", "/ws", "/status", "/metrics" })
        {
            using HttpRequestMessage request = new(HttpMethod.Get, app.Urls.Single() + path);
            if (requestOrigin is not null) request.Headers.Add("Origin", requestOrigin == "local" ? app.Urls.Single() : requestOrigin);
            if (requestHost is not null) request.Headers.Host = requestHost;
            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.That((int)response.StatusCode, Is.EqualTo(expectedStatus), path);
        }
    }

    [Test]
    public async Task Invalid_listener_entries_are_rejected(
        [Values("not-a-url", "http://127.0.0.1:0;not-a-url", "http://0.0.0.0:8545;not-a-url")] string urls)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        Assert.Throws<FormatException>(() => LightClientHost.ConfigureRequestPolicy(app, urls));
    }

    [Test]
    public async Task Wildcard_listener_with_allowed_hosts_rejects_rebinding_and_allows_metrics_and_wallets()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1];light-client";
        builder.Logging.ClearProviders();
        builder.Services.AddCors();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using WebApplication app = builder.Build();
        LightClientHost.ConfigureRequestPolicy(app, "http://0.0.0.0:8545");
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        app.MapGet("/status", status.Read);
        app.MapGet("/metrics", metrics.WriteAsync);
        app.MapPost("/", (HttpContext context) => RpcEndpoint.HandleAsync(context, status.InvokeAsync, NullLogger.Instance, metrics: metrics));
        await app.StartAsync();
        using HttpClient client = new();
        foreach (string host in new[] { "localhost:8545", "127.0.0.1:8545", "[::1]:8545", "light-client:8545" })
        {
            foreach (string path in new[] { "/status", "/metrics" })
            {
                using HttpRequestMessage request = new(HttpMethod.Get, app.Urls.Single() + path);
                request.Headers.Host = host;
                using HttpResponseMessage response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), host + path);
            }
        }

        foreach (string path in new[] { "/status", "/metrics", "/" })
        {
            using HttpRequestMessage request = new(HttpMethod.Get, app.Urls.Single() + path);
            request.Headers.Host = "attacker.example:8545";
            request.Headers.Add("Origin", "http://attacker.example:8545");
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), path);
        }

        using HttpRequestMessage wallet = new(HttpMethod.Post, app.Urls.Single());
        wallet.Headers.Host = "localhost:8545";
        wallet.Headers.Add("Origin", ChromeOrigin);
        wallet.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage result = await client.SendAsync(wallet);
        using JsonDocument chainId = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(chainId.RootElement.GetProperty("result").GetString(), Is.EqualTo("0x1"));
            Assert.That(result.Headers.GetValues("Access-Control-Allow-Origin"), Is.EqualTo(new[] { ChromeOrigin }));
        }
    }

    [TestCase(ChromeOrigin)]
    [TestCase(FirefoxOrigin)]
    public async Task Wallet_extension_can_preflight_and_fetch_chain_id(string origin)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        LightClientHost.ConfigureRequestPolicy(app, "http://127.0.0.1:0");
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        app.MapPost("/", (HttpContext context) => RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance));
        await app.StartAsync();
        using HttpClient client = new();
        using HttpRequestMessage preflight = new(HttpMethod.Options, app.Urls.Single());
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");
        using HttpResponseMessage options = await client.SendAsync(preflight);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(options.Headers.GetValues("Access-Control-Allow-Origin"), Is.EqualTo(new[] { origin }));
            Assert.That(options.Headers.GetValues("Vary"), Does.Contain("Origin"));
            Assert.That(options.Headers.GetValues("Access-Control-Allow-Methods").Single().Split(','), Does.Contain("POST"));
            Assert.That(options.Headers.GetValues("Access-Control-Allow-Headers").Single().Split(','),
                Is.EquivalentTo(new[] { "content-type", "authorization" }));
            Assert.That(options.Headers.Contains("Access-Control-Allow-Credentials"), Is.False);
            Assert.That(options.Headers.Contains("Access-Control-Allow-Private-Network"), Is.False);
        }

        using HttpRequestMessage request = new(HttpMethod.Post, app.Urls.Single());
        request.Headers.Add("Origin", origin);
        request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin"), Is.EqualTo(new[] { origin }));
            Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials"), Is.False);
            Assert.That(json.RootElement.GetProperty("result").GetString(), Is.EqualTo("0x1"));
            Assert.That(json.RootElement.GetProperty("id").GetInt32(), Is.EqualTo(1));
        }
    }

    [TestCase(ChromeOrigin, true)]
    [TestCase(FirefoxOrigin, true)]
    [TestCase("https://wallet.example", false)]
    [TestCase("null", false)]
    public async Task Websocket_upgrade_enforces_the_wallet_origin_policy(string origin, bool allowed)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        LightClientHost.ConfigureRequestPolicy(app, "http://127.0.0.1:0");
        app.UseWebSockets();
        app.MapGet("/ws", async context =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", context.RequestAborted);
        });
        await app.StartAsync();
        using ClientWebSocket client = new();
        client.Options.SetRequestHeader("Origin", origin);
        Uri endpoint = new(app.Urls.Single().Replace("http://", "ws://", StringComparison.Ordinal) + "/ws");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        if (allowed)
        {
            await client.ConnectAsync(endpoint, timeout.Token);
            WebSocketReceiveResult result = await client.ReceiveAsync(new ArraySegment<byte>(new byte[1]), timeout.Token);
            Assert.That(result.CloseStatus, Is.EqualTo(WebSocketCloseStatus.NormalClosure));
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);
        }
        else
        {
            await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectAsync(endpoint, timeout.Token));
            Assert.That(client.State, Is.EqualTo(WebSocketState.Closed));
        }
    }

    [Test]
    public async Task Failed_listener_start_does_not_launch_consensus_work()
    {
        await using WebApplication occupying = CreateApp("http://127.0.0.1:0");
        await occupying.StartAsync();
        await using WebApplication app = CreateApp(occupying.Urls.Single());
        bool launched = false;

        await Assert.CatchAsync<IOException>(async () => await LightClientHost.RunAsync(app, _ =>
        {
            launched = true;
            return Task.CompletedTask;
        }));

        Assert.That(launched, Is.False);
    }

    [Test]
    public async Task Failed_initialization_stops_the_listener([Values] bool synchronous)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");

        await Assert.ThrowsAsync<InvalidDataException>(() => LightClientHost.RunAsync(app, _ => synchronous
            ? throw new InvalidDataException("bootstrap failed")
            : Task.FromException(new InvalidDataException("bootstrap failed"))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(app.Lifetime.ApplicationStopping.IsCancellationRequested, Is.True);
            Assert.That(app.Lifetime.ApplicationStopped.IsCancellationRequested, Is.True);
        }
    }

    [Test]
    public async Task Initialization_timeout_is_not_reclassified_as_normal_application_shutdown()
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");

        InvalidOperationException? failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LightClientHost.RunAsync(app, _ => Task.FromCanceled(new CancellationToken(canceled: true))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.InnerException, Is.InstanceOf<OperationCanceledException>());
            Assert.That(app.Lifetime.ApplicationStopping.IsCancellationRequested, Is.True);
            Assert.That(app.Lifetime.ApplicationStopped.IsCancellationRequested, Is.True);
        }
    }

    [Test]
    public async Task Listener_serves_startup_rpc_and_progress_during_blocked_initialization()
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        LightClientHost.ConfigureRequestPolicy(app, "http://127.0.0.1:0");
        app.UseWebSockets();
        app.MapGet("/status", status.Read);
        app.MapPost("/", (HttpContext context) => RpcEndpoint.HandleAsync(context, status.InvokeAsync, NullLogger.Instance));
        app.MapGet("/ws", status.HandleWebSocketAsync);
        TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = LightClientHost.RunAsync(app, async token =>
        {
            status.SetStage(StartupStage.Bootstrap);
            initialized.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        try
        {
            await initialized.Task;
            using HttpClient client = new();
            using JsonDocument progress = JsonDocument.Parse(await client.GetStringAsync(app.Urls.Single() + "/status"));
            using StringContent request = new("""{"jsonrpc":"2.0","id":1,"method":"eth_syncing","params":[]}""", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(app.Urls.Single(), request);
            using JsonDocument syncing = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            using HttpRequestMessage upgrade = new(HttpMethod.Get, app.Urls.Single() + "/ws");
            upgrade.Headers.Add("Connection", "Upgrade");
            upgrade.Headers.Add("Upgrade", "websocket");
            upgrade.Headers.Add("Sec-WebSocket-Version", "13");
            upgrade.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
            using HttpResponseMessage unavailable = await client.SendAsync(upgrade);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(run.IsCompleted, Is.False);
                Assert.That(progress.RootElement.GetProperty("startupStage").GetString(), Is.EqualTo("bootstrap"));
                Assert.That(progress.RootElement.GetProperty("latest").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(syncing.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Object));
                Assert.That(syncing.RootElement.GetProperty("result").EnumerateObject().Select(item => item.Name),
                    Is.EquivalentTo(new[] { "startingBlock", "currentBlock", "highestBlock" }));
                Assert.That(unavailable.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            }
        }
        finally
        {
            app.Lifetime.StopApplication();
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await run);
        }
    }

    [Test]
    public async Task Application_shutdown_cancels_pending_rpc_work()
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        DefaultHttpContext context = new();
        using MemoryStream input = new(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"eth_getBalance","params":[]}"""));
        context.Request.Body = input;
        using MemoryStream output = new();
        context.Response.Body = output;
        using CancellationTokenSource stopping = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = RpcEndpoint.HandleAsync(context, async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return false;
        }, NullLogger.Instance, stoppingToken: stopping.Token, metrics: metrics);
        await entered.Task;

        await stopping.CancelAsync();

        await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(output.Length, Is.Zero, "Shutdown must not manufacture a successful response.");
        string scrape = await LightClientMetricsTests.ScrapeAsync(metrics);
        using (Assert.EnterMultipleScope())
        {
            LightClientMetricsTests.AssertRpc(scrape, "eth_getBalance", "cancelled", 1);
            LightClientMetricsTests.AssertRpc(scrape, "eth_getBalance", "ok", 0);
            LightClientMetricsTests.AssertRpc(scrape, "eth_getBalance", "error", 0);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    [NonParallelizable]
    public async Task Startup_rpc_wait_observes_http_initialization_and_cancellation_budget(
        [Values("initialize", "shutdown", "deadline", "mixed-cancel")] string outcome, CancellationToken token)
    {
        const string parameters = "[\"0x1000000000000000000000000000000000000001\",\"latest\"]";
        string readRequest = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"eth_getBalance\",\"params\":" + parameters + "}";
        string request = outcome == "mixed-cancel"
            ? "[" + readRequest + ",{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"eth_sendRawTransaction\",\"params\":[\"0xc0\"]}]"
            : readRequest;
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        using MemoryStream input = new(Encoding.UTF8.GetBytes(request));
        using MemoryStream output = new();
        DefaultHttpContext context = new();
        context.Request.Body = input;
        context.Response.Body = output;
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (outcome == "mixed-cancel") context.RequestAborted = stopping.Token;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TimerCallback expire = _ => throw new AssertionException("The endpoint did not create its request deadline.");
        object? timerState = null;
        TimeSpan dueTime = default;
        TimeProvider clock = Substitute.For<TimeProvider>();
        clock.CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>()).Returns(call =>
        {
            expire = call.ArgAt<TimerCallback>(0);
            timerState = call.ArgAt<object?>(1);
            dueTime = call.ArgAt<TimeSpan>(2);
            return Substitute.For<ITimer>();
        });
        List<(string Method, string Parameters)> invoked = [];
        Task pending = RpcEndpoint.HandleAsync(context, (method, values, caller) =>
        {
            try { return status.InvokeAsync(method, values, caller); }
            finally { entered.TrySetResult(); }
        }, NullLogger.Instance, clock, outcome == "mixed-cancel" ? token : stopping.Token);
        await entered.Task.WaitAsync(token);
        bool completedBeforeInitialization = pending.IsCompleted;

        if (outcome == "initialize") Initialize();
        else if (outcome is "shutdown" or "mixed-cancel") await stopping.CancelAsync();
        else expire(timerState);

        if (outcome is "shutdown" or "mixed-cancel")
            Assert.That(async () => await pending.WaitAsync(token), Throws.InstanceOf<OperationCanceledException>());
        else
            await pending.WaitAsync(token);
        int completedCalls = invoked.Count;
        if (outcome != "initialize") Initialize();

        using MemoryStream nextInput = new(Encoding.UTF8.GetBytes(readRequest));
        using MemoryStream nextOutput = new();
        DefaultHttpContext next = new();
        next.Request.Body = nextInput;
        next.Response.Body = nextOutput;
        await RpcEndpoint.HandleAsync(next, status.InvokeAsync, NullLogger.Instance, stoppingToken: token);
        using JsonDocument nextResponse = JsonDocument.Parse(nextOutput.ToArray());
        string nextResult = StringRpcResult(nextResponse);
        if (outcome == "initialize")
        {
            using JsonDocument response = JsonDocument.Parse(output.ToArray());
            Assert.That(StringRpcResult(response), Is.EqualTo("0x2a"));
        }
        else Assert.That(output.Length, Is.Zero, "a canceled startup read must not produce a successful response");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completedBeforeInitialization, Is.False);
            Assert.That(dueTime, Is.EqualTo(TimeSpan.FromSeconds(90)));
            Assert.That(completedCalls, Is.EqualTo(outcome == "initialize" ? 1 : 0));
            if (outcome is "initialize" or "deadline")
                Assert.That(context.Response.StatusCode, Is.EqualTo(outcome == "deadline" ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status200OK));
            if (outcome == "mixed-cancel") Assert.That(context.RequestAborted.IsCancellationRequested, Is.True);
            Assert.That(next.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(nextResult, Is.EqualTo("0x2a"));
            Assert.That(invoked, Is.EqualTo(Enumerable.Repeat(("eth_getBalance", parameters), outcome == "initialize" ? 2 : 1)));
        }

        void Initialize() => status.Initialize((method, values, _) =>
            {
                invoked.Add((method, values.GetRawText()));
                return Task.FromResult<object>("0x2a");
            }, static _ => Task.CompletedTask, static () => null, static () => null,
            static _ => [], static () => new(null, null, null, null, false, null));
    }

    private static string StringRpcResult(JsonDocument response)
    {
        Assert.That(response.RootElement.TryGetProperty("result", out JsonElement result), Is.True, response.RootElement.GetRawText());
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.String));
        return result.GetString()!;
    }

    [Test]
    public async Task Host_waits_for_consensus_work_before_disposal([Values] bool failSync)
    {
        await using WebApplication app = CreateApp("http://127.0.0.1:0");
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = LightClientHost.RunAsync(app, async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => canceled.SetResult());
            ready.SetResult();
            await release.Task;
            if (failSync) throw new InvalidDataException("sync failure");
        });
        await ready.Task;
        app.Lifetime.StopApplication();
        await canceled.Task;
        Assert.That(run.IsCompleted, Is.False);
        release.SetResult();
        if (failSync)
            Assert.That(async () => await run, Throws.TypeOf<InvalidDataException>().With.Message.EqualTo("sync failure"));
        else
            await run;
        Assert.That(() => app.Services.GetService(typeof(ILoggerFactory)), Throws.Nothing);
    }

    private static WebApplication CreateApp(string url)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddCors();
        builder.WebHost.UseUrls(url);
        return builder.Build();
    }

    private static async Task SendInterruptAsync(int clientId, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(OperatingSystem.IsWindows() ? "powershell.exe" : "kill")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($$"""
                $ErrorActionPreference = 'Stop'
                Add-Type -Name ConsoleSignal -Namespace LightClientTests -MemberDefinition @'
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool FreeConsole();
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool AttachConsole(uint pid);
                public delegate bool HandlerRoutine(uint signal);
                public static readonly HandlerRoutine IgnoreInterrupt = _ => true;
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint GetConsoleProcessList(uint[] ids, uint length);
                [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
                '@
                [LightClientTests.ConsoleSignal]::FreeConsole() | Out-Null
                if (![LightClientTests.ConsoleSignal]::AttachConsole({{clientId}})) { throw 'Cannot attach to the test child console.' }
                try {
                    $ids = New-Object uint32[] 8
                    $count = [LightClientTests.ConsoleSignal]::GetConsoleProcessList($ids, 8)
                    $senderId = [Diagnostics.Process]::GetCurrentProcess().Id
                    if ($count -ne 2 -or $ids -notcontains {{clientId}} -or $ids -notcontains $senderId) { throw 'The child console is not isolated.' }
                    if (![LightClientTests.ConsoleSignal]::SetConsoleCtrlHandler([LightClientTests.ConsoleSignal]::IgnoreInterrupt, $true)) { throw 'Cannot protect the signal sender.' }
                    if (![LightClientTests.ConsoleSignal]::GenerateConsoleCtrlEvent(1, 0)) { throw 'Cannot send Ctrl+Break.' }
                    Start-Sleep -Milliseconds 100
                } finally { [LightClientTests.ConsoleSignal]::FreeConsole() | Out-Null }
                """);
        }
        else
        {
            start.ArgumentList.Add("-TERM");
            start.ArgumentList.Add(clientId.ToString());
        }
        using Process sender = Process.Start(start)!;
        try
        {
            await sender.WaitForExitAsync(cancellationToken);
            Assert.That(sender.ExitCode, Is.Zero, "The interrupt must reach only the owned child process.");
        }
        finally
        {
            if (!sender.HasExited) sender.Kill(entireProcessTree: true);
            await sender.WaitForExitAsync();
        }
    }
}

public class LightClientMetricsTests
{
    private const string Prefix = "nethermind_light_client_";

    [Test]
    public async Task Metrics_before_initialization_include_process_series_and_no_available_head(
        [Values("mainnet", "sepolia", "hoodi")] string network)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new(network, status.Read);
        DefaultHttpContext context = new();
        using MemoryStream output = new();
        context.Response.Body = output;

        await metrics.WriteAsync(context);

        string scrape = Encoding.UTF8.GetString(output.ToArray());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.ContentType, Is.EqualTo("text/plain; version=0.0.4; charset=utf-8"));
            Assert.That(Value(scrape, Prefix + "ready", ("network", network)), Is.Zero);
            Assert.That(Value(scrape, Prefix + "initialized"), Is.Zero);
            Assert.That(Value(scrape, Prefix + "startup_stage", ("stage", "checkpoint")), Is.EqualTo(1));
            foreach (string head in new[] { "latest", "finalized" })
                Assert.That(Value(scrape, Prefix + "head_available", ("head", head)), Is.Zero);
            Assert.That(Value(scrape, "process_cpu_seconds_total"), Is.GreaterThanOrEqualTo(0));
            Assert.That(Value(scrape, "process_working_set_bytes"), Is.GreaterThan(0));
            Assert.That(Value(scrape, "process_private_memory_bytes"), Is.GreaterThan(0));
            Assert.That(scrape.Split('\n').Any(line => line.StartsWith(Prefix + "peers{", StringComparison.Ordinal)), Is.False,
                "Connected peers are unobserved until the first sample; bootstrap must not report a false zero.");
        }
    }

    [Test]
    public async Task Metrics_refresh_absolute_snapshots_and_clear_unavailable_or_stopped_state()
    {
        ClientProgress progress = new("running", true, new(100, 90, "latest-hash", true), new(80, 70, "finalized-hash", false),
        [
            new("private-address-one", 90, null, null, 12, 32, true, "ready", null, null),
            new("private-address-two", 80, null, null, 7, 0, false, "failed", 90, "unbounded-error-text"),
        ], null);
        LightClientMetrics metrics = new("mainnet", () => progress);
        metrics.SetPeers(10, 9, 8);

        string running = await ScrapeAsync(metrics);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Value(running, Prefix + "ready"), Is.EqualTo(1));
            Assert.That(Value(running, Prefix + "initialized"), Is.EqualTo(1));
            Assert.That(Value(running, Prefix + "startup_stage", ("stage", "running")), Is.EqualTo(1));
            Assert.That(Value(running, Prefix + "head_block_number", ("head", "latest")), Is.EqualTo(90));
            Assert.That(Value(running, Prefix + "head_beacon_slot", ("head", "latest")), Is.EqualTo(100));
            Assert.That(Value(running, Prefix + "head_fresh", ("head", "latest")), Is.EqualTo(1));
            Assert.That(Value(running, Prefix + "head_fresh", ("head", "finalized")), Is.Zero);
            Assert.That(Value(running, Prefix + "head_available", ("head", "finalized")), Is.EqualTo(1));
            Assert.That(Value(running, Prefix + "hosted_accounts", ("state", "total")), Is.EqualTo(2));
            Assert.That(Value(running, Prefix + "hosted_accounts", ("state", "current")), Is.EqualTo(1));
            Assert.That(Value(running, Prefix + "hosted_accounts", ("state", "failed")), Is.EqualTo(1));
            Assert.That(Value(running, Prefix + "hosted_storage_slots"), Is.EqualTo(19));
            Assert.That(Value(running, Prefix + "peers", ("layer", "beacon")), Is.EqualTo(10));
            Assert.That(Value(running, Prefix + "peers", ("layer", "execution")), Is.EqualTo(9));
            Assert.That(Value(running, Prefix + "peers", ("layer", "snap")), Is.EqualTo(8));
            Assert.That(running, Does.Not.Contain("private-address").And.Not.Contain("unbounded-error-text"));
        }

        progress = new("bootstrap", false, null, null, [], null);
        string missing = await ScrapeAsync(metrics);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Value(missing, Prefix + "ready"), Is.Zero);
            Assert.That(Value(missing, Prefix + "initialized"), Is.Zero);
            Assert.That(Value(missing, Prefix + "startup_stage", ("stage", "running")), Is.Zero);
            Assert.That(Value(missing, Prefix + "startup_stage", ("stage", "bootstrap")), Is.EqualTo(1));
            foreach (string head in new[] { "latest", "finalized" })
            {
                foreach (string metric in new[] { "head_block_number", "head_beacon_slot", "head_fresh", "head_available" })
                    Assert.That(Value(missing, Prefix + metric, ("head", head)), Is.Zero, metric + head);
            }
            foreach (string state in new[] { "total", "current", "failed" })
                Assert.That(Value(missing, Prefix + "hosted_accounts", ("state", state)), Is.Zero);
            Assert.That(Value(missing, Prefix + "hosted_storage_slots"), Is.Zero);
        }

        foreach (string terminal in new[] { "stopping", "failed" })
        {
            progress = progress with { StartupStage = terminal, RpcInitialized = true, Latest = new(100, 90, "retained-head", true) };
            metrics.SetPeers(10, 9, 8);
            string stopped = await ScrapeAsync(metrics);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Value(stopped, Prefix + "ready"), Is.Zero);
                Assert.That(Value(stopped, Prefix + "initialized"), Is.Zero);
                foreach (string layer in new[] { "beacon", "execution", "snap" })
                    Assert.That(Value(stopped, Prefix + "peers", ("layer", layer)), Is.Zero);
            }
        }
    }

    [Test]
    public async Task Metrics_count_each_http_batch_item_notification_and_error_exactly_once()
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        DefaultHttpContext context = new();
        using MemoryStream input = new(Encoding.UTF8.GetBytes("""
            [
              {"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]},
              {"jsonrpc":"2.0","method":"eth_chainId","params":[]},
              {"jsonrpc":"2.0","id":2,"method":"eth_chainId","params":[false]},
              {"jsonrpc":"2.0","id":3,"method":"private_unknown_method","params":[]},
              false
            ]
            """));
        using MemoryStream output = new();
        context.Request.Body = input;
        context.Response.Body = output;

        await RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance, metrics: metrics);

        using JsonDocument response = JsonDocument.Parse(output.ToArray());
        string scrape = await ScrapeAsync(metrics);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(4));
            Assert.That(response.RootElement[0].GetProperty("result").GetString(), Is.EqualTo("0x1"));
            AssertRpc(scrape, "eth_chainId", "ok", 2);
            AssertRpc(scrape, "eth_chainId", "error", 1);
            AssertRpc(scrape, "other", "error", 2);
            Assert.That(scrape, Does.Not.Contain("private_unknown_method"));
        }
    }

    [Test]
    public async Task Http_envelope_failures_have_their_own_metrics(
        [Values("parse_error", "invalid_batch", "too_large")] string outcome)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        byte[] body = outcome switch
        {
            "parse_error" => Encoding.UTF8.GetBytes("{"),
            "invalid_batch" => Encoding.UTF8.GetBytes("[]"),
            _ => new byte[RpcEndpoint.MaxRequestBodySize + 1],
        };
        DefaultHttpContext context = new();
        using MemoryStream input = new(body);
        using MemoryStream output = new();
        context.Request.Body = input;
        context.Response.Body = output;

        await RpcEndpoint.HandleAsync(context, (_, _, _) => throw new AssertionException("Envelope failure must not dispatch an item."),
            NullLogger.Instance, metrics: metrics);

        string scrape = await ScrapeAsync(metrics);
        Assert.That(Value(scrape, Prefix + "http_requests_total", ("outcome", outcome)), Is.EqualTo(1));
        Assert.That(Value(scrape, Prefix + "http_requests_total", ("outcome", "completed")), Is.Zero);
        AssertRpc(scrape, "other", "error", 0);
    }

    [Test]
    [NonParallelizable]
    public async Task Http_overload_is_counted_without_inventing_dispatched_rpc_items()
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>[] active = Enumerable.Range(0, 64).Select(_ => SendAsync()).ToArray();
        try
        {
            Assert.That(await SendAsync(), Is.EqualTo(StatusCodes.Status429TooManyRequests));
            string held = await ScrapeAsync(metrics);
            Assert.That(Value(held, Prefix + "http_requests_total", ("outcome", "overloaded")), Is.EqualTo(1));
            AssertRpc(held, "eth_chainId", "ok", 0);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(active);
        }
        string scrape = await ScrapeAsync(metrics);
        Assert.That(Value(scrape, Prefix + "http_requests_total", ("outcome", "completed")), Is.EqualTo(64));
        AssertRpc(scrape, "eth_chainId", "ok", 64);

        async Task<int> SendAsync()
        {
            DefaultHttpContext context = new();
            using MemoryStream input = new(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"eth_chainId"}"""));
            using MemoryStream output = new();
            context.Request.Body = input;
            context.Response.Body = output;
            await RpcEndpoint.HandleAsync(context, async (_, _, _) => { await release.Task; return "0x1"; }, NullLogger.Instance, metrics: metrics);
            return context.Response.StatusCode;
        }
    }

    [Test]
    public async Task Concurrent_rpc_recording_and_repeated_scrapes_keep_cardinality_bounded()
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        LightClientMetrics metrics = new("mainnet", status.Read);
        string empty = await ScrapeAsync(metrics);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] writers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            await start.Task;
            for (int i = 0; i < 25; i++) metrics.RecordRpc($"unknown_{worker}_{i}", $"unbounded_error_{i}", 0.125);
        })).ToArray();
        Task<string>[] scrapes = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await ScrapeAsync(metrics);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(writers);
        await Task.WhenAll(scrapes);

        string final = await ScrapeAsync(metrics);
        string repeated = await ScrapeAsync(metrics);
        using (Assert.EnterMultipleScope())
        {
            AssertRpc(final, "other", "error", 100);
            Assert.That(Value(final, Prefix + "rpc_duration_seconds_sum", ("method", "other"), ("outcome", "error")), Is.EqualTo(12.5));
            Assert.That(RpcSeries(final), Is.EqualTo(RpcSeries(empty)));
            Assert.That(RpcSeries(repeated), Is.EqualTo(RpcSeries(empty)));
            AssertRpc(repeated, "other", "error", 100);
            Assert.That(final, Does.Not.Contain("unknown_").And.Not.Contain("unbounded_error_"));
        }

        static int RpcSeries(string text) => text.Split('\n').Count(line => line.StartsWith(Prefix + "rpc_", StringComparison.Ordinal));
    }

    internal static async Task<string> ScrapeAsync(LightClientMetrics metrics)
    {
        using MemoryStream output = new();
        await metrics.Registry.CollectAndExportAsTextAsync(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    internal static void AssertRpc(string scrape, string method, string outcome, int count)
    {
        Assert.That(Value(scrape, Prefix + "rpc_requests_total", ("method", method), ("outcome", outcome)), Is.EqualTo(count), method + "/" + outcome);
        Assert.That(Value(scrape, Prefix + "rpc_duration_seconds_count", ("method", method), ("outcome", outcome)), Is.EqualTo(count), method + "/" + outcome + " duration");
    }

    internal static double Value(string scrape, string name, params (string Key, string Value)[] labels)
    {
        string[] lines = scrape.Split('\n').Where(line =>
            (line.StartsWith(name + "{", StringComparison.Ordinal) || line.StartsWith(name + " ", StringComparison.Ordinal)) &&
            labels.All(label => line.Contains($"{label.Key}=\"{label.Value}\"", StringComparison.Ordinal))).ToArray();
        Assert.That(lines, Has.Length.EqualTo(1), name);
        return double.Parse(lines[0].AsSpan(lines[0].LastIndexOf(' ') + 1), CultureInfo.InvariantCulture);
    }
}

public class LightClientStatusTests
{
    [Test]
    public async Task Metadata_is_available_before_authentication_starts(
        [Values("eth_chainId", "net_version", "web3_clientVersion", "eth_accounts", "net_listening")] string method)
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);

        object result = await status.InvokeAsync(method, JsonSerializer.SerializeToElement(Array.Empty<object>()), CancellationToken.None);

        object expected = method switch
        {
            "eth_chainId" => "0xaa36a7",
            "net_version" => "11155111",
            "web3_clientVersion" => ProductInfo.ClientId,
            "eth_accounts" => Array.Empty<string>(),
            _ => false,
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(status.Read().RpcInitialized, Is.False);
        }
    }
    private static readonly Address HostedAddress = new("0x1000000000000000000000000000000000000001");
    private static readonly VerifiedHead Head = new(400, 200, Keccak.Compute("head"), Keccak.Compute("state"));

    [Test]
    public void Startup_reports_unknown_heads_and_individual_waiting_accounts(
        [Values("checkpoint", "beacon", "bootstrap", "execution")] string stageName)
    {
        StartupStage stage = Enum.Parse<StartupStage>(stageName, ignoreCase: true);
        LightClientStatus status = new(11155111, [HostedAddress], _ => false, _ => false);
        status.SetStage(stage);

        ClientProgress progress = status.Read();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.StartupStage, Is.EqualTo(stage.ToString().ToLowerInvariant()));
            Assert.That(progress.RpcInitialized, Is.False);
            Assert.That(progress.Latest, Is.Null);
            Assert.That(progress.Finalized, Is.Null);
            Assert.That(progress.History, Is.Null);
            Assert.That(progress.HostedAccounts, Has.Length.EqualTo(1));
            Assert.That(progress.HostedAccounts[0], Is.EqualTo(new HostedAccountProgress(HostedAddress.ToString(),
                null, null, null, 0, 0, false, "waiting", null, null)));
        }
    }

    [TestCase("eth_chainId", "[]", "\"0xaa36a7\"")]
    [TestCase("eth_syncing", "[]", "{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x0\",\"highestBlock\":\"0x0\"}")]
    public async Task Startup_metadata_preserves_standard_rpc_shapes(string method, string parameters, string expected)
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        using JsonDocument input = JsonDocument.Parse(parameters);

        object result = await status.InvokeAsync(method, input.RootElement, CancellationToken.None);

        Assert.That(JsonSerializer.Serialize(result), Is.EqualTo(expected));
    }

    [Test]
    public void Startup_metadata_rejects_invalid_parameters(
        [Values("eth_chainId", "eth_syncing")] string method, [Values("null", "{}", "[1]")] string parameters)
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        using JsonDocument input = JsonDocument.Parse(parameters);

        Assert.That(async () => await status.InvokeAsync(method, input.RootElement, CancellationToken.None),
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.Code)).EqualTo(-32602));
    }

    [Test]
    public void Startup_transaction_submission_returns_an_actionable_error()
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        status.SetStage(StartupStage.Bootstrap);
        int invoked = 0;

        Assert.That(() => { _ = status.InvokeAsync("eth_sendRawTransaction", default, CancellationToken.None); },
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.Code)).EqualTo(-32000)
                .And.Message.Contains("bootstrap").And.Message.Contains("GET /status"));
        Initialize(status, (_, _, _) => { invoked++; return Task.FromResult<object>("submitted"); });
        Assert.That(invoked, Is.Zero, "a rejected submission must not be queued for initialization");
    }

    [TestCase("eth_blockNumber", "[]")]
    [TestCase("eth_getBalance", "[\"0x1000000000000000000000000000000000000001\",\"latest\"]")]
    [TestCase("eth_getLogs", "[{\"fromBlock\":\"0x1\",\"toBlock\":\"0x2\"}]")]
    [TestCase("unknown_method", "[]")]
    [CancelAfter(10_000)]
    public async Task Startup_requests_wait_for_runtime_and_preserve_arguments(string method, string parameters, CancellationToken token)
    {
        LightClientStatus status = new(11155111, [], _ => false, _ => false);
        using JsonDocument input = JsonDocument.Parse(parameters);
        object expected = new();
        List<(string Method, string Parameters, CancellationToken Token)> invoked = [];
        Task<object> pending = status.InvokeAsync(method, input.RootElement, token);
        bool completedBeforeInitialization = pending.IsCompleted;
        object chainId = await status.InvokeAsync("eth_chainId", default, token);
        object syncing = await status.InvokeAsync("eth_syncing", default, token);
        ClientProgress progress = status.Read();

        Initialize(status, (name, values, caller) =>
        {
            invoked.Add((name, values.GetRawText(), caller));
            return Task.FromResult(expected);
        });
        object result = await pending.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completedBeforeInitialization, Is.False);
            Assert.That(progress.RpcInitialized, Is.False);
            Assert.That(chainId, Is.EqualTo("0xaa36a7"));
            Assert.That(JsonSerializer.Serialize(syncing), Is.EqualTo("{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x0\",\"highestBlock\":\"0x0\"}"));
            Assert.That(result, Is.SameAs(expected));
            Assert.That(invoked, Is.EqualTo(new[] { (method, parameters, token) }));
            Assert.That(status.Read().RpcInitialized, Is.True);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Intermediate_startup_stages_do_not_release_pending_requests(CancellationToken token)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        Task<object> pending = status.InvokeAsync("eth_blockNumber", default, token);
        List<bool> completed = [];
        foreach (StartupStage stage in new[] { StartupStage.Checkpoint, StartupStage.Beacon, StartupStage.Bootstrap, StartupStage.Execution })
        {
            status.SetStage(stage);
            completed.Add(pending.IsCompleted);
        }
        Initialize(status, static (_, _, _) => Task.FromResult<object>("verified"));
        object result = await pending.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.EqualTo(new[] { false, false, false, false }));
            Assert.That(result, Is.EqualTo("verified"));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Running_dispatch_returns_the_runtime_task_directly(
        [Values("eth_blockNumber", "eth_chainId", "eth_syncing", "eth_sendRawTransaction")] string method, CancellationToken token)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        TaskCompletionSource<object> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<object> expected = completion.Task;
        Initialize(status, (_, _, _) => expected);
        Task<object> actual = status.InvokeAsync(method, default, token);
        try
        {
            using IDisposable scope = Assert.EnterMultipleScope();
            Assert.That(actual, Is.SameAs(expected));
            Assert.That(actual.IsCompleted, Is.False);
        }
        finally
        {
            completion.TrySetResult(new object());
            await ((Task)actual).WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Canceling_one_startup_request_does_not_cancel_other_waiters(CancellationToken token)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<object> canceled = status.InvokeAsync("eth_getBalance", default, caller.Token);
        Task<object> survivor = status.InvokeAsync("eth_blockNumber", default, token);
        await caller.CancelAsync();
        bool cancellationObserved;
        try { await canceled.WaitAsync(token); cancellationObserved = false; }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { cancellationObserved = true; }
        bool survivorCompleted = survivor.IsCompleted;
        List<string> invoked = [];
        Initialize(status, (method, _, _) => { invoked.Add(method); return Task.FromResult<object>("verified"); });
        object result = await survivor.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancellationObserved, Is.True);
            Assert.That(survivorCompleted, Is.False);
            Assert.That(result, Is.EqualTo("verified"));
            Assert.That(invoked, Is.EqualTo(new[] { "eth_blockNumber" }));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Terminal_startup_wakes_waiters_and_rejects_late_initialization(
        [Values("stopping", "failed")] string stageName, CancellationToken token)
    {
        StartupStage terminal = Enum.Parse<StartupStage>(stageName, ignoreCase: true);
        LightClientStatus status = new(1, [], _ => false, _ => false);
        Task<object> pending = status.InvokeAsync("eth_getBalance", default, token);
        status.SetStage(terminal);
        Initialize(status, static (_, _, _) => throw new AssertionException("Terminal startup must not publish runtime services."));
        status.SetStage(StartupStage.Execution);

        Assert.That(async () => await pending.WaitAsync(token), Throws.TypeOf<RpcException>()
            .With.Property(nameof(RpcException.Code)).EqualTo(-32000).And.Message.Contains(terminal.ToString().ToLowerInvariant()));
        Assert.That(async () => await status.InvokeAsync("eth_blockNumber", default, token), Throws.TypeOf<RpcException>()
            .With.Property(nameof(RpcException.Code)).EqualTo(-32000));
        ClientProgress progress = status.Read();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.StartupStage, Is.EqualTo(terminal.ToString().ToLowerInvariant()));
            Assert.That(progress.RpcInitialized, Is.False);
        }
    }

    [Test]
    public async Task Running_status_keeps_freshness_cache_and_history_coverage_separate()
    {
        ulong slot = Head.Slot;
        LightClientStatus status = new(11155111, [HostedAddress],
            head => head?.IsFresh(slot, 10) ?? false, head => head?.IsFresh(slot, 300) ?? false);
        HostedAccountProgress account = new(HostedAddress.ToString(), Head.Number - 1, Keccak.Compute("previous").ToString(),
            Head.StateRoot.ToString(), 3, 4, true, "complete", null, null);
        HistoryProgress history = new(100, 200, 50, 200, true, null);
        bool invoked = false;
        status.Initialize((_, _, _) => { invoked = true; return Task.FromResult<object>("verified"); },
            _ => Task.CompletedTask, () => Head, () => Head, target =>
            {
                Assert.That(target, Is.EqualTo(Head));
                return [account];
            }, () => history);

        ClientProgress current = status.Read();
        slot += 11;
        ClientProgress staleLatest = status.Read();
        slot += 290;
        ClientProgress staleFinalized = status.Read();
        object result = await status.InvokeAsync("eth_blockNumber", default, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(current.StartupStage, Is.EqualTo("running"));
            Assert.That(current.RpcInitialized, Is.True);
            Assert.That(current.Latest!.BlockNumber, Is.EqualTo(Head.Number));
            Assert.That(current.Latest.Fresh, Is.True);
            Assert.That(staleLatest.Latest!.Fresh, Is.False);
            Assert.That(staleLatest.Finalized!.Fresh, Is.True);
            Assert.That(staleFinalized.Finalized!.Fresh, Is.False);
            Assert.That(staleLatest.HostedAccounts, Is.EqualTo(new[] { account }));
            Assert.That(staleLatest.History, Is.SameAs(history));
            Assert.That(invoked, Is.True);
            Assert.That(result, Is.EqualTo("verified"));
        }
    }

    [Test]
    public void Stopped_or_failed_runtime_rejects_data_without_touching_disposed_services(
        [Values("stopping", "failed")] string stageName)
    {
        StartupStage stage = Enum.Parse<StartupStage>(stageName, ignoreCase: true);
        LightClientStatus status = new(1, [], _ => false, _ => false);
        status.Initialize((_, _, _) => throw new AssertionException("Disposed RPC must not be called."),
            _ => throw new AssertionException("Disposed subscriptions must not be called."),
            () => null, () => null, _ => [], () => new(null, null, null, null, false, null));
        status.SetStage(stage);

        Assert.That(async () => await status.InvokeAsync("eth_getBalance", default, CancellationToken.None),
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.Code)).EqualTo(-32000));
    }

    [Test]
    public void Startup_rpc_observes_request_cancellation(
        [Values("eth_chainId", "eth_syncing", "eth_getBalance", "eth_sendRawTransaction")] string method)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        using CancellationTokenSource stop = new();
        stop.Cancel();

        Assert.That(async () => await status.InvokeAsync(method, default, stop.Token),
            Throws.TypeOf<OperationCanceledException>());
    }

    private static void Initialize(LightClientStatus status, Func<string, JsonElement, CancellationToken, Task<object>> invoke) =>
        status.Initialize(invoke, static _ => Task.CompletedTask, static () => null, static () => null,
            static _ => [], static () => new(null, null, null, null, false, null));
}
