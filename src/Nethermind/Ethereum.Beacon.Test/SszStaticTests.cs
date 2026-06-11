// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.BeaconChain.Types;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;

namespace Ethereum.Beacon.Test;

[TestFixture]
public class SszStaticTests
{
    private static readonly string[] Forks = ["electra", "fulu"];

    private interface ISszStaticHandler
    {
        void Run(byte[] serialized, UInt256 expectedRoot);
    }

    private sealed class Handler<T> : ISszStaticHandler where T : ISszCodec<T>
    {
        public void Run(byte[] serialized, UInt256 expectedRoot)
        {
            T.Decode(serialized, out T decoded);
            T.Merkleize(decoded, out UInt256 root);
            byte[] reEncoded = T.Encode(decoded);

            Assert.Multiple(() =>
            {
                Assert.That(root, Is.EqualTo(expectedRoot), "Hash tree root mismatch");
                Assert.That(reEncoded, Is.EqualTo(serialized), "Re-encoded SSZ does not match original");
            });
        }
    }

    private sealed class HandlerMap() : Dictionary<string, ISszStaticHandler>(StringComparer.Ordinal)
    {
        public HandlerMap Add<T>(string? name = null) where T : ISszCodec<T>
        {
            this[name ?? typeof(T).Name] = new Handler<T>();
            return this;
        }
    }

    /// <summary>Handlers shared by all forks; <c>BeaconState</c> is resolved per fork in <see cref="GetHandler"/>.</summary>
    private static readonly HandlerMap CommonHandlers = new HandlerMap()
        .Add<AggregateAndProof>()
        .Add<Attestation>()
        .Add<AttestationData>()
        .Add<AttesterSlashing>()
        .Add<BeaconBlock>()
        .Add<BeaconBlockBody>()
        .Add<BeaconBlockHeader>()
        .Add<BlsToExecutionChange>("BLSToExecutionChange")
        .Add<Checkpoint>()
        .Add<ConsolidationRequest>()
        .Add<Deposit>()
        .Add<DepositData>()
        .Add<DepositMessage>()
        .Add<DepositRequest>()
        .Add<Eth1Data>()
        .Add<ExecutionPayload>()
        .Add<ExecutionPayloadHeader>()
        .Add<ExecutionRequests>()
        .Add<Fork>()
        .Add<ForkData>()
        .Add<HistoricalSummary>()
        .Add<IndexedAttestation>()
        .Add<PendingConsolidation>()
        .Add<PendingDeposit>()
        .Add<PendingPartialWithdrawal>()
        .Add<ProposerSlashing>()
        .Add<SignedAggregateAndProof>()
        .Add<SignedBeaconBlock>()
        .Add<SignedBeaconBlockHeader>()
        .Add<SignedBlsToExecutionChange>("SignedBLSToExecutionChange")
        .Add<SignedVoluntaryExit>()
        .Add<SigningData>()
        .Add<SyncAggregate>()
        .Add<SyncCommittee>()
        .Add<Validator>()
        .Add<VoluntaryExit>()
        .Add<Withdrawal>()
        .Add<WithdrawalRequest>();

    private static readonly ISszStaticHandler ElectraStateHandler = new Handler<BeaconStateElectra>();
    private static readonly ISszStaticHandler FuluStateHandler = new Handler<BeaconStateFulu>();

    private static ISszStaticHandler GetHandler(string fork, string handler) => (handler, fork) switch
    {
        ("BeaconState", "fulu") => FuluStateHandler,
        ("BeaconState", _) => ElectraStateHandler,
        _ => CommonHandlers[handler],
    };

    [TestCaseSource(nameof(SszStaticCases))]
    public void Ssz_static_roundtrip_and_root(string fork, string handler, string casePath)
    {
        byte[] serialized = BeaconConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, "serialized.ssz_snappy"));
        UInt256 expectedRoot = BeaconConsensusTestLoader.ParseRoot(Path.Combine(casePath, "roots.yaml"));

        GetHandler(fork, handler).Run(serialized, expectedRoot);
    }

    private static IEnumerable<TestCaseData> SszStaticCases()
    {
        foreach (string fork in Forks)
        {
            foreach (string handler in GetHandlerNames())
            {
                string handlerPath = BeaconConsensusTestLoader.GetHandlerPath(fork, handler);
                if (!Directory.Exists(handlerPath))
                    continue;

                foreach (string suitePath in Directory.GetDirectories(handlerPath))
                {
                    foreach (string casePath in Directory.GetDirectories(suitePath))
                    {
                        yield return new TestCaseData(fork, handler, casePath)
                            .SetName($"{fork}/{handler}/{Path.GetFileName(suitePath)}/{Path.GetFileName(casePath)}");
                    }
                }
            }
        }
    }

    private static IEnumerable<string> GetHandlerNames()
    {
        foreach (string name in CommonHandlers.Keys)
        {
            yield return name;
        }

        yield return "BeaconState";
    }
}
