// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Nethermind.BeaconChain.Api;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.Types;
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

internal sealed class BeaconApiTestHost : IAsyncDisposable
{
    public BeaconChainSpec Spec { get; }
    public BeaconChainStatusHolder StatusHolder { get; }
    internal SlotClock Clock { get; }
    internal ManualTimestamper Timestamper { get; }
    public IColumnsDb<BeaconChainDbColumns> Db { get; }
    public BeaconChainStore Store { get; }
    public BeaconApiHost Host { get; }
    public HttpClient Client { get; private set; } = null!;
    public PeerManager? PeerManager { get; }

    private BeaconApiTestHost(BeaconChainSpec spec, ForkChoiceSnapshotHolder? forkChoiceSnapshots, IColumnsDb<BeaconChainDbColumns>? db, BeaconApiConfig? apiConfig, ILogManager? logManager, bool withPeerManager, HeadSnapshotHolder? headSnapshots, IEngineDriver? engine, bool forkAwareStore)
    {
        Spec = spec;
        Db = db ?? new MemColumnsDb<BeaconChainDbColumns>();
        Timestamper = new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)spec.GenesisTime).UtcDateTime);
        StatusHolder = new BeaconChainStatusHolder(spec, Timestamper);
        Store = new BeaconChainStore(Db, forkAwareStore ? spec : null);
        Clock = new SlotClock(spec, Timestamper);
        BeaconChainConfig chainConfig = new();
        LocalMetadataSource metadataSource = new();
        if (withPeerManager)
        {
            BeaconP2P p2p = new(chainConfig, spec, Store, StatusHolder, metadataSource, new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance);
            PeerManager = new PeerManager(p2p, chainConfig, StatusHolder, LimboLogs.Instance);
        }

        apiConfig ??= new BeaconApiConfig();
        apiConfig.Enabled = true;
        apiConfig.Host = "127.0.0.1";
        apiConfig.Port = 0;
        Host = new BeaconApiHost(apiConfig, chainConfig, spec, StatusHolder, Clock, Store,
            metadataSource, engine ?? new NoOpEngineDriver(), new NoOpProcessExitSource(), logManager ?? LimboLogs.Instance, peerManager: PeerManager, forkChoiceSnapshots: forkChoiceSnapshots, headSnapshots: headSnapshots);
    }

    public static async Task<BeaconApiTestHost> StartAsync(BeaconChainSpec spec, ForkChoiceSnapshotHolder? forkChoiceSnapshots = null,
        IColumnsDb<BeaconChainDbColumns>? db = null, BeaconApiConfig? apiConfig = null, ILogManager? logManager = null, bool withPeerManager = false, HeadSnapshotHolder? headSnapshots = null,
        IEngineDriver? engine = null, bool forkAwareStore = true)
    {
        BeaconApiTestHost host = new(spec, forkChoiceSnapshots, db, apiConfig, logManager, withPeerManager, headSnapshots, engine, forkAwareStore);
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

    public void WriteLegacyBlock(Hash256 root, SignedBeaconBlock block) =>
        Db.GetColumnDb(BeaconChainDbColumns.Blocks).Set(root.Bytes, Snappy.CompressToArray(SignedBeaconBlock.Encode(block)));

    /// <summary>The fixed part and slot of a signed beacon block with no body after them: its slot reads, but it never decodes.</summary>
    public static byte[] SlotPrefixOnlyBlockSsz(ulong slot)
    {
        byte[] ssz = new byte[sizeof(uint) + BlsSignature.Length + sizeof(ulong)];
        BinaryPrimitives.WriteUInt32LittleEndian(ssz, sizeof(uint) + BlsSignature.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(ssz.AsSpan(sizeof(uint) + BlsSignature.Length), slot);
        return ssz;
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    public static async Task<string> ReadSuccessfulBodyAsync(HttpResponseMessage response)
    {
        string raw = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), raw);
        return raw;
    }

    public static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.That(response.StatusCode, Is.EqualTo(expected));
        using JsonDocument body = await ReadJsonAsync(response);
        Assert.That(body.RootElement.GetProperty("code").GetInt32(), Is.EqualTo((int)expected));
    }

    public static Hash256 TestRoot(byte marker) => TestHashes.FromLow(marker);
    public static Hash256 FilledHash(byte fill) => new(Filled(32, fill));
    public static BlsPublicKey FilledPubkey(byte fill) => new(Filled(48, fill));
    public static BlsSignature FilledSignature(byte fill) => new(Filled(96, fill));
    public static string Hex(int length, byte fill) => Bytes.ToHexString(Filled(length, fill), withZeroX: true);
    private static byte[] Filled(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();

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

        Hash256[] proof = Enumerable.Range(0, 33).Select(static i => FilledHash((byte)(0x30 + i))).ToArray();

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

    public static BeaconStateFulu MinimalState(BeaconChainSpec spec, ulong slot, Fork fork, Validator[] validators, ulong[] balances, Hash256[] randaoMixes) => new()
    {
        GenesisTime = spec.GenesisTime,
        GenesisValidatorsRoot = spec.GenesisValidatorsRoot,
        Slot = slot,
        Fork = fork,
        LatestBlockHeader = new BeaconBlockHeader { Slot = slot - 1, ProposerIndex = 0, ParentRoot = Hash256.Zero, StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero },
        Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
        Validators = validators,
        Balances = balances,
        RandaoMixes = randaoMixes,
        Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
        JustificationBits = new BitArray(4),
        PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
        CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
        FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
        ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
        LatestExecutionPayloadHeader = new ExecutionPayloadHeader
        {
            ParentHash = Hash256.Zero,
            FeeRecipient = Address.Zero,
            StateRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            LogsBloom = Bloom.Empty,
            PrevRandao = Hash256.Zero,
            ExtraData = [],
            BlockHash = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            WithdrawalsRoot = Hash256.Zero,
        },
    };

    public static BeaconStateFulu RichState(BeaconChainSpec spec, ulong slot)
    {
        Hash256[] randaoMixes = Enumerable.Repeat(FilledHash(0x42), (int)Presets.EpochsPerHistoricalVector).ToArray();
        Hash256[] blockRoots = Enumerable.Repeat(FilledHash(0x43), 8192).ToArray();
        Hash256[] stateRoots = Enumerable.Repeat(FilledHash(0x44), 8192).ToArray();
        BlsPublicKey[] committee = Enumerable.Repeat(FilledPubkey(0x45), 512).ToArray();
        BitArray justificationBits = new(4);
        justificationBits[0] = true;
        justificationBits[3] = true;
        ulong[] lookahead = Enumerable.Range(0, (int)Presets.ProposerLookaheadSlots).Select(static i => (ulong)(i % 3)).ToArray();
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

    public static SignedBeaconBlock MinimalBlock(ulong slot)
    {
        SignedBeaconBlock block = SignedBeaconBlockBuilders.CreateMinimalBlock(slot);
        block.Message!.ProposerIndex = 0;
        ExecutionPayload payload = block.Message.Body!.ExecutionPayload!;
        payload.BlockNumber = 1;
        payload.GasUsed = 0;
        payload.Timestamp = 1_606_824_023;
        payload.ExtraData = [];
        return block;
    }
}

internal sealed class NoOpEngineDriver : IEngineDriver
{
    public SignedBeaconBlock? CurrentBlock { get; set; }
    public bool HasAnsweredNewPayload { get; set; }
    public bool IsAvailable { get; set; } = true;

    public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
        Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid });

    public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
}

internal sealed class NoOpProcessExitSource : IProcessExitSource
{
    private readonly CancellationTokenSource _cts = new();
    public void Exit(int exitCode) => _cts.Cancel();
    public CancellationToken Token => _cts.Token;
}
