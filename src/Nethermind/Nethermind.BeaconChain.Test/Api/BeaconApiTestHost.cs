// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Snappier;
using ExecutionPayload = Nethermind.BeaconChain.Types.ExecutionPayload;
using Transaction = Nethermind.BeaconChain.Types.Transaction;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.Test.Api;

/// <summary>
/// A real Kestrel host on an ephemeral port over an in-memory store, for endpoint tests that need
/// to put blocks and states in and read HTTP out. Also builds the deliberately busy block and state
/// fixtures the JSON body tests compare against.
/// </summary>
internal sealed class BeaconApiTestHost : IAsyncDisposable
{
    public BeaconChainSpec Spec { get; }
    public BeaconChainStatusHolder StatusHolder { get; }
    public MemColumnsDb<BeaconChainDbColumns> Db { get; } = new();
    public BeaconChainStore Store { get; }
    public BeaconApiHost Host { get; }
    public HttpClient Client { get; private set; } = null!;

    private BeaconApiTestHost(BeaconChainSpec spec, ForkChoiceSnapshotHolder? forkChoiceSnapshots)
    {
        Spec = spec;
        ManualTimestamper timestamper = new(DateTimeOffset.FromUnixTimeSeconds((long)spec.GenesisTime).UtcDateTime);
        StatusHolder = new BeaconChainStatusHolder(spec, timestamper);
        Store = new BeaconChainStore(Db);
        BeaconApiConfig apiConfig = new() { Enabled = true, Host = "127.0.0.1", Port = 0 };
        Host = new BeaconApiHost(apiConfig, new BeaconChainConfig(), spec, StatusHolder, new SlotClock(spec, timestamper), Store,
            new LocalMetadataSource(), new NoOpEngineDriver(), new NoOpProcessExitSource(), LimboLogs.Instance, forkChoiceSnapshots: forkChoiceSnapshots);
    }

    /// <param name="forkChoiceSnapshots">The holder the debug fork-choice endpoint reads; <c>null</c> runs the host without one, as the driver-less configurations do.</param>
    public static async Task<BeaconApiTestHost> StartAsync(BeaconChainSpec spec, ForkChoiceSnapshotHolder? forkChoiceSnapshots = null)
    {
        BeaconApiTestHost host = new(spec, forkChoiceSnapshots);
        await host.Host.StartAsync(CancellationToken.None);
        host.Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Host.Port}"), Timeout = TimeSpan.FromSeconds(30) };
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.DisposeAsync();
        Db.Dispose();
    }

    public void SetStatus(Hash256 head, Hash256 finalizedRoot, ulong finalizedEpoch) =>
        StatusHolder.CurrentStatus = new StatusMessageV2 { ForkDigest = [], HeadRoot = head, FinalizedRoot = finalizedRoot, FinalizedEpoch = finalizedEpoch };

    public Task<HttpResponseMessage> GetAsync(string path, string accept)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return Client.SendAsync(request);
    }

    /// <summary>Writes a block exactly as the pre-index store did, bypassing the children index.</summary>
    public void WriteLegacyBlock(Hash256 root, SignedBeaconBlock block) =>
        Db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(root.Bytes, Snappy.CompressToArray(SignedBeaconBlock.Encode(block)));

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    public static Hash256 TestRoot(byte marker)
    {
        byte[] bytes = new byte[32];
        bytes[31] = marker;
        return new Hash256(bytes);
    }

    public static Hash256 FilledHash(byte fill)
    {
        byte[] bytes = new byte[32];
        Array.Fill(bytes, fill);
        return new Hash256(bytes);
    }

    public static BlsPublicKey FilledPubkey(byte fill)
    {
        byte[] bytes = new byte[48];
        Array.Fill(bytes, fill);
        return new BlsPublicKey(bytes);
    }

    public static BlsSignature FilledSignature(byte fill)
    {
        byte[] bytes = new byte[96];
        Array.Fill(bytes, fill);
        return new BlsSignature(bytes);
    }

    public static string Hex(int length, byte fill) => Bytes.ToHexString(Filled(length, fill), withZeroX: true);

    private static byte[] Filled(int length, byte fill)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    /// <summary>A block with one of every body operation populated, so a field the writer forgets or mislabels has somewhere to be missed from.</summary>
    public static SignedBeaconBlock RichBlock(ulong slot, Hash256 parent)
    {
        BitArray aggregationBits = new(3);
        aggregationBits[0] = true;
        aggregationBits[2] = true;
        BitArray committeeBits = new(64);
        committeeBits[1] = true;
        BitArray syncBits = new(512);
        syncBits[0] = true;
        syncBits[511] = true;

        AttestationData attestationData = new()
        {
            Slot = slot - 1,
            Index = 0,
            BeaconBlockRoot = FilledHash(0xaa),
            Source = new Checkpoint { Epoch = 412_498, Root = FilledHash(0xab) },
            Target = new Checkpoint { Epoch = 412_499, Root = FilledHash(0xac) },
        };

        SignedBeaconBlockHeader header1 = new()
        {
            Message = new BeaconBlockHeader { Slot = slot - 2, ProposerIndex = 5, ParentRoot = FilledHash(0x11), StateRoot = FilledHash(0x12), BodyRoot = FilledHash(0x13) },
            Signature = FilledSignature(0x14),
        };
        SignedBeaconBlockHeader header2 = new()
        {
            Message = new BeaconBlockHeader { Slot = slot - 2, ProposerIndex = 5, ParentRoot = FilledHash(0x21), StateRoot = FilledHash(0x22), BodyRoot = FilledHash(0x23) },
            Signature = FilledSignature(0x24),
        };

        Hash256[] proof = new Hash256[33];
        for (int i = 0; i < proof.Length; i++) proof[i] = FilledHash((byte)(0x30 + i));

        return new SignedBeaconBlock
        {
            Message = new BeaconBlock
            {
                Slot = slot,
                ProposerIndex = 77,
                ParentRoot = parent,
                StateRoot = FilledHash(0x01),
                Body = new BeaconBlockBody
                {
                    RandaoReveal = FilledSignature(0x02),
                    Eth1Data = new Eth1Data { DepositRoot = FilledHash(0x03), DepositCount = 1234, BlockHash = FilledHash(0x04) },
                    Graffiti = FilledHash(0x05),
                    ProposerSlashings = [new ProposerSlashing { SignedHeader1 = header1, SignedHeader2 = header2 }],
                    AttesterSlashings =
                    [
                        new AttesterSlashing
                        {
                            Attestation1 = new IndexedAttestation { AttestingIndices = [1, 2, 3], Data = attestationData, Signature = FilledSignature(0x41) },
                            Attestation2 = new IndexedAttestation { AttestingIndices = [2, 3], Data = attestationData, Signature = FilledSignature(0x42) },
                        },
                    ],
                    Attestations = [new Attestation { AggregationBits = aggregationBits, Data = attestationData, Signature = FilledSignature(0x43), CommitteeBits = committeeBits }],
                    Deposits =
                    [
                        new Deposit
                        {
                            Proof = proof,
                            Data = new DepositData { Pubkey = FilledPubkey(0x51), WithdrawalCredentials = FilledHash(0x52), Amount = 32_000_000_000, Signature = FilledSignature(0x53) },
                        },
                    ],
                    VoluntaryExits = [new SignedVoluntaryExit { Message = new VoluntaryExit { Epoch = 412_400, ValidatorIndex = 9 }, Signature = FilledSignature(0x61) }],
                    SyncAggregate = new SyncAggregate { SyncCommitteeBits = syncBits, SyncCommitteeSignature = FilledSignature(0x71) },
                    ExecutionPayload = new ExecutionPayload
                    {
                        ParentHash = FilledHash(0x81),
                        FeeRecipient = new Address(Filled(20, 0x82)),
                        StateRoot = FilledHash(0x83),
                        ReceiptsRoot = FilledHash(0x84),
                        LogsBloom = new Bloom(Filled(256, 0x85)),
                        PrevRandao = FilledHash(0x86),
                        BlockNumber = 23_000_000,
                        GasLimit = 30_000_000,
                        GasUsed = 21_000,
                        Timestamp = 1_750_000_000,
                        ExtraData = Bytes.FromHexString("0xc0ffee"),
                        BaseFeePerGas = 7,
                        BlockHash = FilledHash(0x87),
                        Transactions = [new Transaction { Bytes = Bytes.FromHexString("0x02f870") }, new Transaction { Bytes = Bytes.FromHexString("0x01") }],
                        Withdrawals = [new Withdrawal { Index = 100, ValidatorIndex = 200, Address = new Address(Filled(20, 0x88)), Amount = 300 }],
                        BlobGasUsed = 131_072,
                        ExcessBlobGas = 0,
                    },
                    BlsToExecutionChanges =
                    [
                        new SignedBlsToExecutionChange
                        {
                            Message = new BlsToExecutionChange { ValidatorIndex = 11, FromBlsPubkey = FilledPubkey(0x91), ToExecutionAddress = new Address(Filled(20, 0x92)) },
                            Signature = FilledSignature(0x93),
                        },
                    ],
                    BlobKzgCommitments = [SszKzgCommitment.FromSpan(Filled(48, 0xa1)), SszKzgCommitment.FromSpan(Filled(48, 0xa2))],
                    ExecutionRequests = new ExecutionRequests
                    {
                        Deposits = [new DepositRequest { Pubkey = FilledPubkey(0xb1), WithdrawalCredentials = FilledHash(0xb2), Amount = 1_000_000_000, Signature = FilledSignature(0xb3), Index = 42 }],
                        Withdrawals = [new WithdrawalRequest { SourceAddress = new Address(Filled(20, 0xc1)), ValidatorPubkey = FilledPubkey(0xc2), Amount = 5 }],
                        Consolidations = [new ConsolidationRequest { SourceAddress = new Address(Filled(20, 0xd1)), SourcePubkey = FilledPubkey(0xd2), TargetPubkey = FilledPubkey(0xd3) }],
                    },
                },
            },
            Signature = FilledSignature(0x06),
        };
    }

    /// <summary>A Fulu state with a few validators and every list non-empty, so each list field is exercised with real elements.</summary>
    public static BeaconStateFulu RichState(BeaconChainSpec spec, ulong slot)
    {
        Hash256[] randaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector];
        Array.Fill(randaoMixes, FilledHash(0x42));
        Hash256[] blockRoots = new Hash256[8192];
        Array.Fill(blockRoots, FilledHash(0x43));
        Hash256[] stateRoots = new Hash256[8192];
        Array.Fill(stateRoots, FilledHash(0x44));
        BlsPublicKey[] committee = new BlsPublicKey[512];
        Array.Fill(committee, FilledPubkey(0x45));
        BitArray justificationBits = new(4);
        justificationBits[0] = true;
        justificationBits[3] = true;
        ulong[] lookahead = new ulong[(int)Presets.ProposerLookaheadSlots];
        for (int i = 0; i < lookahead.Length; i++) lookahead[i] = (ulong)(i % 3);
        ulong[] slashings = new ulong[(int)Presets.EpochsPerSlashingsVector];
        slashings[1] = 64_000_000_000;

        Validator[] validators =
        [
            new Validator { Pubkey = FilledPubkey(0x01), WithdrawalCredentials = FilledHash(0x02), EffectiveBalance = 32_000_000_000, Slashed = false, ActivationEligibilityEpoch = 0, ActivationEpoch = 1, ExitEpoch = Presets.FarFutureEpoch, WithdrawableEpoch = Presets.FarFutureEpoch },
            new Validator { Pubkey = FilledPubkey(0x03), WithdrawalCredentials = FilledHash(0x04), EffectiveBalance = 31_000_000_000, Slashed = true, ActivationEligibilityEpoch = 2, ActivationEpoch = 3, ExitEpoch = 412_600, WithdrawableEpoch = 420_000 },
            new Validator { Pubkey = FilledPubkey(0x05), WithdrawalCredentials = FilledHash(0x06), EffectiveBalance = 2_048_000_000_000, Slashed = false, ActivationEligibilityEpoch = 4, ActivationEpoch = 5, ExitEpoch = Presets.FarFutureEpoch, WithdrawableEpoch = Presets.FarFutureEpoch },
        ];

        return new BeaconStateFulu
        {
            GenesisTime = spec.GenesisTime,
            GenesisValidatorsRoot = spec.GenesisValidatorsRoot,
            Slot = slot,
            Fork = new Fork { PreviousVersion = [5, 0, 0, 0], CurrentVersion = [6, 0, 0, 0], Epoch = 411_392 },
            LatestBlockHeader = new BeaconBlockHeader { Slot = slot - 1, ProposerIndex = 8, ParentRoot = FilledHash(0x11), StateRoot = Hash256.Zero, BodyRoot = FilledHash(0x13) },
            BlockRoots = blockRoots,
            StateRoots = stateRoots,
            HistoricalRoots = [FilledHash(0x46)],
            Eth1Data = new Eth1Data { DepositRoot = FilledHash(0x21), DepositCount = 999, BlockHash = FilledHash(0x22) },
            Eth1DataVotes = [new Eth1Data { DepositRoot = FilledHash(0x23), DepositCount = 1000, BlockHash = FilledHash(0x24) }],
            Eth1DepositIndex = 998,
            Validators = validators,
            Balances = [32_000_000_000, 30_999_999_999, 2_048_000_000_001],
            RandaoMixes = randaoMixes,
            Slashings = slashings,
            PreviousEpochParticipation = [1, 3, 7],
            CurrentEpochParticipation = [0, 2, 255],
            JustificationBits = justificationBits,
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 412_497, Root = FilledHash(0x31) },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 412_498, Root = FilledHash(0x32) },
            FinalizedCheckpoint = new Checkpoint { Epoch = 412_497, Root = FilledHash(0x33) },
            InactivityScores = [0, 4, 0],
            CurrentSyncCommittee = new SyncCommittee { Pubkeys = committee, AggregatePubkey = FilledPubkey(0x47) },
            NextSyncCommittee = new SyncCommittee { Pubkeys = committee, AggregatePubkey = FilledPubkey(0x48) },
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader
            {
                ParentHash = FilledHash(0x51),
                FeeRecipient = new Address(Filled(20, 0x52)),
                StateRoot = FilledHash(0x53),
                ReceiptsRoot = FilledHash(0x54),
                LogsBloom = new Bloom(Filled(256, 0x55)),
                PrevRandao = FilledHash(0x56),
                BlockNumber = 22_999_999,
                GasLimit = 30_000_000,
                GasUsed = 12_345,
                Timestamp = 1_749_999_988,
                ExtraData = Bytes.FromHexString("0xbeef"),
                BaseFeePerGas = 1_000_000_007,
                BlockHash = FilledHash(0x57),
                TransactionsRoot = FilledHash(0x58),
                WithdrawalsRoot = FilledHash(0x59),
                BlobGasUsed = 262_144,
                ExcessBlobGas = 131_072,
            },
            NextWithdrawalIndex = 5_000_000,
            NextWithdrawalValidatorIndex = 2,
            HistoricalSummaries = [new HistoricalSummary { BlockSummaryRoot = FilledHash(0x61), StateSummaryRoot = FilledHash(0x62) }],
            DepositRequestsStartIndex = 1_900_000,
            DepositBalanceToConsume = 10,
            ExitBalanceToConsume = 20,
            EarliestExitEpoch = 412_600,
            ConsolidationBalanceToConsume = 30,
            EarliestConsolidationEpoch = 412_601,
            PendingDeposits = [new PendingDeposit { Pubkey = FilledPubkey(0x71), WithdrawalCredentials = FilledHash(0x72), Amount = 1_000_000_000, Signature = FilledSignature(0x73), Slot = slot - 5 }],
            PendingPartialWithdrawals = [new PendingPartialWithdrawal { ValidatorIndex = 2, Amount = 1_000, WithdrawableEpoch = 412_700 }],
            PendingConsolidations = [new PendingConsolidation { SourceIndex = 0, TargetIndex = 2 }],
            ProposerLookahead = lookahead,
        };
    }

    private sealed class NoOpEngineDriver : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }
        public bool HasAnsweredNewPayload => false;

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid });

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }

    private sealed class NoOpProcessExitSource : IProcessExitSource
    {
        private readonly CancellationTokenSource _cts = new();
        public void Exit(int exitCode) => _cts.Cancel();
        public CancellationToken Token => _cts.Token;
    }
}
