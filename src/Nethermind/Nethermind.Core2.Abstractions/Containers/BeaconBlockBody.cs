//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Crypto.Hash32;

namespace Nethermind.Core2.Containers
{
    public class BeaconBlockBody
    {
        private readonly List<Attestation> _attestations;
        private readonly List<AttesterSlashing> _attesterSlashings;
        private readonly List<Deposit> _deposits;
        private readonly List<ProposerSlashing> _proposerSlashings;
        private readonly List<VoluntaryExit> _voluntaryExits;

        public BeaconBlockBody(
            BlsSignature randaoReveal,
            Eth1Data eth1Data,
            Bytes32 graffiti,
            IEnumerable<ProposerSlashing> proposerSlashings,
            IEnumerable<AttesterSlashing> attesterSlashings,
            IEnumerable<Attestation> attestations,
            IEnumerable<Deposit> deposits,
            IEnumerable<VoluntaryExit> voluntaryExits)
        {
            RandaoReveal = randaoReveal;
            Eth1Data = eth1Data;
            Graffiti = graffiti;
            _proposerSlashings = new List<ProposerSlashing>(proposerSlashings);
            _attesterSlashings = new List<AttesterSlashing>(attesterSlashings);
            _attestations = new List<Attestation>(attestations);
            _deposits = new List<Deposit>(deposits);
            _voluntaryExits = new List<VoluntaryExit>(voluntaryExits);
        }

        public BeaconBlockBody()
        {
            RandaoReveal = BlsSignature.Empty;
            Eth1Data = new Eth1Data(0, Hash32.Zero);
            Graffiti = new Bytes32();
            _proposerSlashings = new List<ProposerSlashing>();
            _attesterSlashings = new List<AttesterSlashing>();
            _attestations = new List<Attestation>();
            _deposits = new List<Deposit>();
            _voluntaryExits = new List<VoluntaryExit>();
        }

        public IReadOnlyList<Attestation> Attestations { get { return _attestations; } }
        public IReadOnlyList<AttesterSlashing> AttesterSlashings { get { return _attesterSlashings; } }
        public IReadOnlyList<Deposit> Deposits { get { return _deposits; } }
        public Eth1Data Eth1Data { get; }
        public Bytes32 Graffiti { get; private set; }
        public IReadOnlyList<ProposerSlashing> ProposerSlashings { get { return _proposerSlashings; } }
        public BlsSignature RandaoReveal { get; private set; }

        public IReadOnlyList<VoluntaryExit> VoluntaryExits { get { return _voluntaryExits; } }

        public void AddAttestations(Attestation attestation) => _attestations.Add(attestation);

        public void SetGraffiti(Bytes32 graffiti) => Graffiti = graffiti;

        public void SetRandaoReveal(BlsSignature randaoReveal) => RandaoReveal = randaoReveal;

        public override string ToString()
        {
            return $"R:{RandaoReveal.ToString().Substring(0, 12)} A[{Attestations.Count}] AS[{AttesterSlashings.Count}] D[{Deposits.Count}] PS[{ProposerSlashings.Count}]";
        }

        /*
        public IList<Transfer> Transfers { get; }
        */
    }
}
