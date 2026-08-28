// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using System.Net;
using System.Text.Json;

namespace Nethermind.Bootnode;

internal sealed class BootnodeNodeRecordProvider(
    IProtectedPrivateKey nodeKey,
    IEthereumEcdsa ethereumEcdsa,
    INetworkConfig networkConfig,
    IIPResolver.NethermindIp resolvedIp,
    string dataDir) : INodeRecordProvider
{
    private readonly Lock _lock = new();
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
        SetEndpointEntries(selfNodeRecord, resolvedIp.ExternalIpV4, resolvedIp.ExternalIpV6);
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

    private void SetEndpointEntries(NodeRecord selfNodeRecord, IPAddress? externalIpV4, IPAddress? externalIpV6)
    {
        if (externalIpV4 is not null)
        {
            selfNodeRecord.SetEntry(new IpEntry(externalIpV4));
            if (networkConfig.P2PPort > 0)
            {
                selfNodeRecord.SetEntry(new TcpEntry(networkConfig.P2PPort));
            }

            selfNodeRecord.SetEntry(new UdpEntry(networkConfig.DiscoveryPort));
        }

        if (externalIpV6 is not null)
        {
            selfNodeRecord.SetEntry(new Ip6Entry(externalIpV6));
            if (networkConfig.P2PPort > 0)
            {
                selfNodeRecord.SetEntry(new Tcp6Entry(networkConfig.P2PPort));
            }

            selfNodeRecord.SetEntry(new Udp6Entry(networkConfig.DiscoveryPort));
        }
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

        EnrSequenceState? state;
        try
        {
            await using FileStream stream = File.OpenRead(_sequenceStatePath);
            state = await JsonSerializer.DeserializeAsync<EnrSequenceState>(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw CreateInvalidSequenceStateException(exception);
        }
        catch (IOException exception)
        {
            throw new IOException(
                $"Unable to read ENR sequence state '{_sequenceStatePath}'. Resolve the filesystem error and retry.",
                exception);
        }

        if (state is null || string.IsNullOrEmpty(state.ContentHash) || state.EnrSequence == 0)
        {
            throw CreateInvalidSequenceStateException();
        }

        return state;
    }

    private InvalidDataException CreateInvalidSequenceStateException(Exception? innerException = null)
    {
        string message = $"ENR sequence state '{_sequenceStatePath}' is invalid. " +
            "Restore a valid state file. Deleting it resets the ENR sequence to 1, which cached peers may ignore as stale; " +
            "rotate the node key if immediate recovery is required.";
        return innerException is null
            ? new InvalidDataException(message)
            : new InvalidDataException(message, innerException);
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
