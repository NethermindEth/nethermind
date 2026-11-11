// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Autofac;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.LightClient.Test;

[NonParallelizable]
public class MobileIdentityTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "NethermindLightClientMobileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task Fresh_identity_is_small_and_persists_without_a_wallet_keystore(
        [Values(null, "network.key")] string? configuredFile)
    {
        string fileName = configuredFile ?? "node.key.plain";
        string path = Path.Combine(_directory, fileName);
        PublicKey first;
        await using (ExecutionPeerTransport transport = CreateTransport())
        {
            IContainer services = Services(transport);
            services.Resolve<IKeyStoreConfig>().EnodeKeyFile = configuredFile;
            long before = GC.GetTotalAllocatedBytes(precise: true);
            IProtectedPrivateKey identity = services.ResolveKeyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            first = identity.PublicKey;
            using PrivateKey unprotected = identity.Unprotect();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(allocated, Is.LessThan(32 * 1024 * 1024), "A networking identity must not run wallet scrypt derivation.");
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(unprotected.KeyBytes));
                Assert.That(Directory.GetFiles(_directory), Is.EquivalentTo(new[] { path }));
                Assert.That(Directory.GetFiles(_directory, "UTC--*", SearchOption.AllDirectories), Is.Empty);
                Assert.That(services.IsRegistered<IKeyStore>(), Is.False);
                if (!OperatingSystem.IsWindows())
                    Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }
        }

        await using ExecutionPeerTransport restarted = CreateTransport();
        IContainer restartedServices = Services(restarted);
        restartedServices.Resolve<IKeyStoreConfig>().EnodeKeyFile = configuredFile;
        IProtectedPrivateKey reloaded = restartedServices.ResolveKeyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey);
        Assert.That(reloaded.PublicKey, Is.EqualTo(first));
    }

    [Test]
    public async Task Corrupt_identity_fails_without_replacing_the_file([Values(0, 31, 32, 33)] int length)
    {
        string path = Path.Combine(_directory, "node.key.plain");
        byte[] corrupt = new byte[length];
        File.WriteAllBytes(path, corrupt);
        await using ExecutionPeerTransport transport = CreateTransport();
        IContainer services = Services(transport);
        Exception exception = Assert.Throws<Autofac.Core.DependencyResolutionException>(
            () => services.ResolveKeyed<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.GetBaseException(), Is.InstanceOf<ArgumentException>());
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(corrupt));
            Assert.That(Directory.GetFiles(_directory), Is.EquivalentTo(new[] { path }));
        }
    }

    private ExecutionPeerTransport CreateTransport() => new("mainnet", NullLogManager.Instance,
        new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), dataDirectory: _directory);

    private static IContainer Services(ExecutionPeerTransport transport) => (IContainer)typeof(ExecutionPeerTransport)
        .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
}
