// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Autofac;
using DotNetty.Buffers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.LightClient.Mobile;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Stats.Model;
using NSubstitute;

namespace Nethermind.LightClient.Test;

public class MobileTransportTests
{
    [Test]
    public async Task Mobile_execution_composes_download_protocols_without_a_full_node(
        [Values("mainnet", "sepolia", "hoodi")] string network)
    {
        string directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "NethermindLightClientMobileTests", Guid.NewGuid().ToString("N"));
        Hash256 genesis = network switch
        {
            "mainnet" => KnownHashes.MainnetGenesis,
            "sepolia" => KnownHashes.SepoliaGenesis,
            _ => KnownHashes.HoodiGenesis,
        };
        Directory.CreateDirectory(directory);
        try
        {
            await using ExecutionPeerTransport transport = new(network, NullLogManager.Instance,
                new VerifiedHead(0, 0, genesis, Hash256.Zero), dataDirectory: directory);
            IContainer services = (IContainer)typeof(ExecutionPeerTransport)
                .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
            SnapEnrAdvertisement advertisement = services.Resolve<SnapEnrAdvertisement>();
            IProtocolsManager protocols = services.Resolve<IProtocolsManager>();
            ISession session = Substitute.For<ISession>();
            using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
            session.Node.Returns(new Node(key.PublicKey, "127.0.0.1", 30303));
            IProtocolHandlerFactory[] factories = services.Resolve<IProtocolHandlerFactory[]>();
            foreach ((string protocol, int version) in new (string, int)[]
                { (Protocol.Eth, 68), (Protocol.Eth, 69), (Protocol.Eth, 70), (Protocol.Eth, 71), (Protocol.Eth, 72), (Protocol.Snap, 1), (Protocol.Snap, 2) })
            {
                IProtocolHandler? handler = null;
                foreach (IProtocolHandlerFactory factory in factories)
                    if (factory.ProtocolCode == protocol && factory.TryCreate(session, version, out handler)) break;
                Assert.That(handler, Is.Not.Null, $"{network}: {protocol}/{version}");
                handler!.Dispose();
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(protocols.GetHighestProtocolVersion(Protocol.Snap), Is.EqualTo(2));
                Assert.That(advertisement.ServesSnap, Is.False);
                Assert.That(advertisement.ServesSnap2, Is.False);
                Assert.That(services.Resolve<ISyncConfig>().SnapServingEnabled, Is.False);
                Assert.That(services.Resolve<INetworkConfig>().DiscoveryDns, Does.EndWith($"@snap.{network}.ethdisco.net"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Mobile_buffers_release_bursts_without_limiting_message_size(CancellationToken token)
    {
        const string ChildProcess = "NETHERMIND_MOBILE_BUFFER_TEST";
        if (Environment.GetEnvironmentVariable(ChildProcess) == "1")
        {
            MobileClient.ConfigureNetworkBuffers();
            AssertBufferRetention();
            return;
        }

        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(MobileTransportTests).Assembly.Location);
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add($"FullyQualifiedName~{nameof(Mobile_buffers_release_bursts_without_limiting_message_size)}");
        start.Environment[ChildProcess] = "1";
        start.Environment["io.netty.allocator.tinyCacheSize"] = "512";
        start.Environment["io.netty.allocator.smallCacheSize"] = "256";
        start.Environment["io.netty.allocator.normalCacheSize"] = "64";
        start.Environment["io.netty.allocator.maxCachedBufferCapacity"] = "32768";
        start.Environment["io.netty.allocator.cacheTrimInterval"] = "8192";
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
    private static void AssertBufferRetention()
    {
        PooledByteBufferAllocator allocator = PooledByteBufferAllocator.Default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NethermindBuffers.Default, Is.SameAs(allocator));
            Assert.That(NethermindBuffers.RlpxAllocator, Is.SameAs(allocator));
            Assert.That(NethermindBuffers.DiscoveryAllocator, Is.SameAs(allocator));
        }
        foreach (bool direct in new[] { false, true })
        {
            foreach ((int length, int retainedLimit) in new[] { (64, 32), (1024, 16), (8192, 8), (32768, 0) })
            {
                PooledByteBufferAllocator burstAllocator = new();
                IReadOnlyList<IPoolArenaMetric> burstArenas = direct ? burstAllocator.Metric.DirectArenas() : burstAllocator.Metric.HeapArenas();
                IByteBuffer?[] buffers = new IByteBuffer[128];
                try
                {
                    for (int i = 0; i < buffers.Length; i++)
                    {
                        IByteBuffer buffer = direct ? burstAllocator.DirectBuffer(length, length) : burstAllocator.HeapBuffer(length, length);
                        buffers[i] = buffer;
                        buffer.WriteZero(length);
                        buffer.SetInt(0, i);
                        buffer.SetInt(length - 4, ~i);
                    }
                    using (Assert.EnterMultipleScope())
                    {
                        for (int i = 0; i < buffers.Length; i++)
                        {
                            Assert.That(buffers[i]!.GetInt(0), Is.EqualTo(i));
                            Assert.That(buffers[i]!.GetInt(length - 4), Is.EqualTo(~i));
                        }
                    }
                }
                finally
                {
                    foreach (IByteBuffer? buffer in buffers) buffer?.Release();
                }
                Assert.That(burstArenas.Sum(static arena => arena.NumActiveAllocations),
                    Is.LessThanOrEqualTo(retainedLimit), $"{(direct ? "direct" : "heap")} {length}-byte released burst");
            }

            IReadOnlyList<IPoolArenaMetric> arenas = direct ? allocator.Metric.DirectArenas() : allocator.Metric.HeapArenas();
            long hugeBefore = arenas.Sum(static arena => arena.NumActiveHugeAllocations);
            IByteBuffer oversized = direct ? allocator.DirectBuffer(64, 16 * 1024 * 1024) : allocator.HeapBuffer(64, 16 * 1024 * 1024);
            try
            {
                oversized.WriteZero(64);
                oversized.SetInt(0, 0x534e4150);
                oversized.WriteZero(16 * 1024 * 1024 - 64);
                oversized.SetInt(oversized.WriterIndex - 4, 0x45544832);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(oversized.ReadableBytes, Is.EqualTo(16 * 1024 * 1024));
                    Assert.That(oversized.GetInt(0), Is.EqualTo(0x534e4150));
                    Assert.That(oversized.GetInt(oversized.WriterIndex - 4), Is.EqualTo(0x45544832));
                }
            }
            finally { oversized.Release(); }
            Assert.That(arenas.Sum(static arena => arena.NumActiveHugeAllocations), Is.EqualTo(hugeBefore));
        }
    }
}
