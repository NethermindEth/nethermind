// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Enr;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>Builds and re-signs the local beacon chain discv5 ENR with the <c>eth2</c> and <c>nfd</c> entries.</summary>
/// <remarks>
/// The TCP port advertises the libp2p host while the UDP port advertises discv5, both signed with the
/// persisted p2p identity so the ENR's secp256k1 key matches the libp2p peer id. <see cref="Update"/>
/// bumps the sequence number, which is how peers learn about an EIP-7892 BPO digest rotation.
/// </remarks>
public sealed class BeaconNodeRecordProvider : INodeRecordProvider
{
    private readonly PrivateKey _key;
    private readonly IPAddress _externalIp;
    private readonly int _tcpPort;
    private readonly int _udpPort;
    private readonly ulong _custodyGroupCount;
    private readonly NodeRecordSigner _signer;
    private readonly Lock _updateLock = new();
    private volatile NodeRecord _current;
    private EnrForkId _forkId;
    private byte[] _nextForkDigest;

    public BeaconNodeRecordProvider(PrivateKey key, IPAddress externalIp, int tcpPort, int udpPort, EnrForkId forkId, ulong custodyGroupCount, byte[]? nextForkDigest = null)
    {
        _key = key;
        _externalIp = externalIp;
        _tcpPort = tcpPort;
        _udpPort = udpPort;
        _custodyGroupCount = custodyGroupCount;
        _signer = new NodeRecordSigner(new Ecdsa(), key);
        _forkId = forkId;
        _nextForkDigest = nextForkDigest ?? NfdEntry.NoneScheduled;
        _current = Build(forkId, _nextForkDigest, sequence: 1);
    }

    public NodeRecord Current => _current;

    /// <inheritdoc/>
    /// <remarks>The record is built and signed eagerly, so there is nothing to await here.</remarks>
    public ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_current);

    public EnrForkId ForkId
    {
        get
        {
            lock (_updateLock)
            {
                return _forkId;
            }
        }
    }

    /// <summary>The currently advertised <c>nfd</c> value: <see cref="NfdEntry.NoneScheduled"/> when no fork is upcoming.</summary>
    public byte[] NextForkDigest
    {
        get
        {
            lock (_updateLock)
            {
                return _nextForkDigest;
            }
        }
    }

    /// <summary>
    /// Replaces the <c>eth2</c> and <c>nfd</c> entries and bumps the ENR sequence when either changed.
    /// </summary>
    /// <param name="nextForkDigest">
    /// The digest to advertise under <c>nfd</c>, or <see langword="null"/> when no fork is scheduled
    /// (published as <see cref="NfdEntry.NoneScheduled"/>).
    /// </param>
    /// <returns><see langword="true"/> when a new record was published; <see langword="false"/> when unchanged.</returns>
    public bool Update(EnrForkId forkId, byte[]? nextForkDigest = null)
    {
        byte[] normalizedNextDigest = nextForkDigest ?? NfdEntry.NoneScheduled;
        lock (_updateLock)
        {
            if (forkId.Equals(_forkId) && Bytes.AreEqual(normalizedNextDigest, _nextForkDigest))
            {
                return false;
            }

            _forkId = forkId;
            _nextForkDigest = normalizedNextDigest;
            _current = Build(forkId, normalizedNextDigest, _current.EnrSequence + 1);
            return true;
        }
    }

    private NodeRecord Build(EnrForkId forkId, byte[] nextForkDigest, ulong sequence)
    {
        NodeRecord record = new();
        record.SetEntry(new IpEntry(_externalIp));
        record.SetEntry(new TcpEntry(_tcpPort));
        record.SetEntry(new UdpEntry(_udpPort));
        record.SetEntry(new SecP256k1Entry(_key.CompressedPublicKey));
        record.SetEntry(new Eth2Entry(forkId.Encode()));
        record.SetEntry(new CustodyGroupCountEntry(_custodyGroupCount));
        record.SetEntry(new NfdEntry(nextForkDigest));
        record.EnrSequence = sequence;
        _signer.Sign(record);
        return record;
    }
}
