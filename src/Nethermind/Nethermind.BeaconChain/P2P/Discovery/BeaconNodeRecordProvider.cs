// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using Nethermind.Crypto;
using Nethermind.Network.Discovery;
using Nethermind.Network.Enr;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>Builds and re-signs the local beacon chain discv5 ENR with the <c>eth2</c> fork id entry.</summary>
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
    private readonly NodeRecordSigner _signer;
    private readonly Lock _updateLock = new();
    private volatile NodeRecord _current;
    private EnrForkId _forkId;

    public BeaconNodeRecordProvider(PrivateKey key, IPAddress externalIp, int tcpPort, int udpPort, EnrForkId forkId)
    {
        _key = key;
        _externalIp = externalIp;
        _tcpPort = tcpPort;
        _udpPort = udpPort;
        _signer = new NodeRecordSigner(new Ecdsa(), key);
        _forkId = forkId;
        _current = Build(forkId, sequence: 1);
    }

    public NodeRecord Current => _current;

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

    /// <summary>Replaces the <c>eth2</c> entry and bumps the ENR sequence when the fork id changed.</summary>
    /// <returns><see langword="true"/> when a new record was published; <see langword="false"/> when unchanged.</returns>
    public bool Update(EnrForkId forkId)
    {
        lock (_updateLock)
        {
            if (forkId.Equals(_forkId))
            {
                return false;
            }

            _forkId = forkId;
            _current = Build(forkId, _current.EnrSequence + 1);
            return true;
        }
    }

    private NodeRecord Build(EnrForkId forkId, ulong sequence)
    {
        NodeRecord record = new();
        record.SetEntry(new IpEntry(_externalIp));
        record.SetEntry(new TcpEntry(_tcpPort));
        record.SetEntry(new UdpEntry(_udpPort));
        record.SetEntry(new SecP256k1Entry(_key.CompressedPublicKey));
        record.SetEntry(new Eth2Entry(forkId.Encode()));
        record.EnrSequence = sequence;
        _signer.Sign(record);
        return record;
    }
}
