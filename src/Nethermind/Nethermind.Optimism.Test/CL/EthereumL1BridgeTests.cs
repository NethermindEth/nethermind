// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.L1Bridge;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class EthereumL1BridgeTests
{
    private static readonly Address BatchSubmitter = TestItem.AddressA;
    private static readonly Address BatcherInbox = TestItem.AddressB;

    [OneTimeSetUp]
    public Task OneTimeSetUp() => KzgPolynomialCommitments.InitializeAsync();

    [Test]
    public async Task Step_returns_the_blobs_of_a_batcher_transaction()
    {
        ShardBlobNetworkWrapper wrapper = CreateBlobs(1, 2);

        L1BridgeStepResult result = await StepOverBlobTransaction(ToSidecars(wrapper), GetVersionedHashes(wrapper));

        Assert.That(result.NewData, Is.Not.Null);
        Assert.That(result.NewData!, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.NewData![0].Data, Is.SameAs(wrapper.Blobs[0]));
            Assert.That(result.NewData![1].Data, Is.SameAs(wrapper.Blobs[1]));
        }
    }

    [Test]
    public void Step_fails_when_sidecar_commitments_do_not_match_the_versioned_hashes()
    {
        ShardBlobNetworkWrapper wrapper = CreateBlobs(1, 2);
        BlobSidecar[] sidecars = ToSidecars(wrapper);
        (sidecars[0], sidecars[1]) = (sidecars[1], sidecars[0]);

        Assert.That(() => StepOverBlobTransaction(sidecars, GetVersionedHashes(wrapper)), Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Step_fails_when_a_blob_does_not_match_its_commitment()
    {
        ShardBlobNetworkWrapper wrapper = CreateBlobs(1, 2);
        BlobSidecar[] sidecars = ToSidecars(wrapper);
        sidecars[1].Blob = CreateBlobs(3, 1).Blobs[0];

        Assert.That(() => StepOverBlobTransaction(sidecars, GetVersionedHashes(wrapper)), Throws.InstanceOf<InvalidDataException>());
    }

    private static async Task<L1BridgeStepResult> StepOverBlobTransaction(BlobSidecar[] sidecars, byte[][] versionedHashes)
    {
        L1Block finalized = new() { Number = 10, Hash = TestItem.KeccakA, ParentHash = TestItem.KeccakB };
        L1Block head = new()
        {
            Number = 11,
            Hash = TestItem.KeccakC,
            ParentHash = finalized.Hash,
            Timestamp = 120,
            Transactions =
            [
                new L1Transaction
                {
                    Hash = TestItem.KeccakD,
                    Type = TxType.Blob,
                    From = BatchSubmitter,
                    To = BatcherInbox,
                    BlobVersionedHashes = versionedHashes
                }
            ]
        };

        IEthApi ethApi = Substitute.For<IEthApi>();
        ethApi.GetFinalized(true).Returns(Task.FromResult<L1Block?>(finalized));
        ethApi.GetHead(true).Returns(Task.FromResult<L1Block?>(head));
        IBeaconApi beaconApi = Substitute.For<IBeaconApi>();
        beaconApi.GetBlobSidecars(10, 0, versionedHashes.Length - 1, Arg.Any<CancellationToken>()).Returns(sidecars);

        EthereumL1Bridge bridge = new(ethApi, beaconApi,
            new CLChainSpecEngineParameters { BatchSubmitter = BatchSubmitter, BatcherInboxAddress = BatcherInbox, L1BeaconGenesisSlotTime = 0 },
            LimboLogs.Instance);
        await bridge.Initialize(CancellationToken.None);
        return await bridge.Step(CancellationToken.None);
    }

    private static ShardBlobNetworkWrapper CreateBlobs(int seed, int count)
    {
        byte[][] blobs = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            blobs[i] = new byte[Eip4844Constants.GasPerBlob];
            blobs[i][31] = (byte)(seed + i);
        }

        IBlobProofsManager proofsManager = IBlobProofsManager.For(ProofVersion.V0);
        ShardBlobNetworkWrapper wrapper = proofsManager.AllocateWrapper(blobs);
        proofsManager.ComputeProofsAndCommitments(wrapper);
        return wrapper;
    }

    private static BlobSidecar[] ToSidecars(ShardBlobNetworkWrapper wrapper)
    {
        BlobSidecar[] sidecars = new BlobSidecar[wrapper.Blobs.Length];
        for (int i = 0; i < sidecars.Length; i++)
        {
            sidecars[i] = new BlobSidecar
            {
                Blob = wrapper.Blobs[i],
                KzgCommitment = wrapper.Commitments[i],
                KzgProof = wrapper.Proofs[(i * ShardBlobNetworkWrapper.BytesPerProof)..((i + 1) * ShardBlobNetworkWrapper.BytesPerProof)]
            };
        }

        return sidecars;
    }

    private static byte[][] GetVersionedHashes(ShardBlobNetworkWrapper wrapper)
    {
        byte[][] hashes = new byte[wrapper.Commitments.Length][];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = new byte[Eip4844Constants.BytesPerBlobVersionedHash];
            KzgPolynomialCommitments.TryComputeCommitmentHashV1(wrapper.Commitments[i], hashes[i]);
        }

        return hashes;
    }
}
