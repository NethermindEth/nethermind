// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Enr;
using System.Text.Json;

namespace Nethermind.Bootnode;

internal sealed class BootnodeNodeRecordProvider(
    IProtectedPrivateKey nodeKey,
    IIPResolver ipResolver,
    IEthereumEcdsa ethereumEcdsa,
    INetworkConfig networkConfig,
    ILogManager logManager,
    string dataDir) : INodeRecordProvider
{
    private readonly Lock _lock = new();
    private readonly Nethermind.Logging.ILogger _logger = logManager.GetClassLogger<BootnodeNodeRecordProvider>();
    private readonly string _sequenceStatePath = Path.Combine(dataDir, "enr-state.json");
    private Task<NodeRecord>? _nodeRecordTask;

    public ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        Task<NodeRecord>? task = Volatile.Read(ref _nodeRecordTask);
        if (task is null)
        {
            lock (_lock)
            {
                task = _nodeRecordTask ??= PrepareNodeRecord(CancellationToken.None);
            }
        }

        return new ValueTask<NodeRecord>(task.WaitAsync(cancellationToken));
    }

    private async Task<NodeRecord> PrepareNodeRecord(CancellationToken cancellationToken)
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        IIPResolver.NethermindIp ip = await ipResolver.Resolve(cancellationToken);
        selfNodeRecord.SetEntry(new IpEntry(ip.ExternalIp));
        if (networkConfig.P2PPort > 0)
        {
            selfNodeRecord.SetEntry(new TcpEntry(networkConfig.P2PPort));
        }

        selfNodeRecord.SetEntry(new UdpEntry(networkConfig.DiscoveryPort));
        selfNodeRecord.SetEntry(new SecP256k1Entry(nodeKey.CompressedPublicKey));
        selfNodeRecord.EnrSequence = 0;
        string contentHash = selfNodeRecord.ContentHash.ToString();
        selfNodeRecord.EnrSequence = await GetSequenceAsync(contentHash, cancellationToken);

        using PrivateKey privateKey = nodeKey.Unprotect();
        NodeRecordSigner enrSigner = new(ethereumEcdsa, privateKey);
        enrSigner.Sign(selfNodeRecord);
        if (!enrSigner.Verify(selfNodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }

        return selfNodeRecord;
    }

    private async Task<ulong> GetSequenceAsync(string contentHash, CancellationToken cancellationToken)
    {
        EnrSequenceState? state = await ReadSequenceStateAsync(cancellationToken);
        ulong sequence = state is null
            ? 1
            : state.ContentHash == contentHash
                ? state.EnrSequence
                : GetNextSequence(state.EnrSequence);

        await WriteSequenceStateAsync(new EnrSequenceState(contentHash, sequence), cancellationToken);
        return sequence;
    }

    private async Task<EnrSequenceState?> ReadSequenceStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_sequenceStatePath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(_sequenceStatePath);
            return await JsonSerializer.DeserializeAsync<EnrSequenceState>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            if (_logger.IsWarn) _logger.Warn($"Unable to load ENR sequence state '{_sequenceStatePath}': {exception.Message}");
            return null;
        }
    }

    private async Task WriteSequenceStateAsync(EnrSequenceState state, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_sequenceStatePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{_sequenceStatePath}.tmp";
        try
        {
            await using (FileStream stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, _sequenceStatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static ulong GetNextSequence(ulong previousSequence)
        => previousSequence == ulong.MaxValue
            ? throw new InvalidOperationException("Cannot increment ENR sequence beyond UInt64.MaxValue.")
            : previousSequence + 1;

    private sealed record EnrSequenceState(string ContentHash, ulong EnrSequence);
}
