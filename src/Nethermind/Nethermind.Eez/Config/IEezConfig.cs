// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Eez.Config;

[ConfigCategory(Description = "EEZ rollup L2 execution rules")]
public interface IEezConfig : IConfig
{
    [ConfigItem(Description = "Whether to execute the chain under the EEZ L2 rules. Requires an EEZ genesis.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Whether to follow the rollup from L1: derive every L2 block from the batches L1 settled. Requires `Eez.Enabled`. " +
        "The follower must be the only driver of the node's engine API: a consensus client that moves the forkchoice shuts the node down.", DefaultValue = "false")]
    bool FollowerEnabled { get; set; }

    [ConfigItem(Description = "The JSON-RPC URL of the rollup's sequencer. When set, the follower also takes the sequencer's latest blocks as the " +
        "unsafe head before L1 settles them; the safe and finalized heads still follow L1 alone.", DefaultValue = "null")]
    string? SequencerRpcUrl { get; set; }

    [ConfigItem(Description = "The JSON-RPC URL of the L1 execution client the follower reads batches from. Required when `Eez.FollowerEnabled` is set.", DefaultValue = "null")]
    string? L1RpcUrl { get; set; }

    [ConfigItem(Description = "The chain ID the L1 execution client must report. Required when `Eez.FollowerEnabled` is set.", DefaultValue = "0")]
    ulong L1ChainId { get; set; }

    [ConfigItem(Description = "The address of the EEZ registry contract on L1. Required when `Eez.FollowerEnabled` is set.", DefaultValue = "null")]
    string? RegistryAddress { get; set; }

    [ConfigItem(Description = "The L1 block the EEZ registry was deployed in; the follower scans L1 from it. Required when `Eez.FollowerEnabled` is set.", DefaultValue = "0")]
    ulong RegistryDeployBlock { get; set; }

    [ConfigItem(Description = "The rollup ID of this L2 in the EEZ registry. Required when `Eez.FollowerEnabled` is set.", DefaultValue = "0")]
    ulong RollupId { get; set; }

    [ConfigItem(Description = "The L2 block time, in whole seconds: derivation adds it to each parent's timestamp.", DefaultValue = "2")]
    ulong L2BlockTimeSeconds { get; set; }

    [ConfigItem(Description = "The gas limit derivation gives every L2 block.", DefaultValue = "30000000")]
    ulong L2GasLimit { get; set; }

    [ConfigItem(Description = "How often the follower polls L1 for new blocks, in milliseconds.", DefaultValue = "2000")]
    int L1PollingIntervalMs { get; set; }

    [ConfigItem(Description = "The most L1 blocks the follower reads logs for in one request.", DefaultValue = "100000")]
    ulong L1LogScanBlocks { get; set; }

    [ConfigItem(Description = "Whether to sequence the rollup: produce its blocks on the L1 slot schedule and post the batches that settle them. " +
        "Requires `Eez.FollowerEnabled`, which confirms what L1 settled, and excludes `Eez.SequencerRpcUrl`.", DefaultValue = "false")]
    bool SequencerEnabled { get; set; }

    [ConfigItem(Description = "The L1 block time in milliseconds; an L1 slot holds this over the L2 block time L2 blocks.", DefaultValue = "12000")]
    uint L1BlockTimeMs { get; set; }

    [ConfigItem(Description = "The most time attesters take to prove a slot, in milliseconds: the slot is composed at least this long, plus " +
        "the submission slack and a margin to produce the Sync block, before the next L1 block, rounded up to whole L2 blocks.", DefaultValue = "2000")]
    uint ProofTimeMs { get; set; }

    [ConfigItem(Description = "How long before the next L1 block a proven batch must reach the builder, in milliseconds.", DefaultValue = "100")]
    uint SubmissionSlackMs { get; set; }

    [ConfigItem(Description = "The attesters asked to sign each batch, as `url=attester=proofSystem` entries: the prove.v1 endpoint, the address " +
        "it signs with and the proof system that verifies it. Required when `Eez.SequencerEnabled` is set; at most 16.", DefaultValue = "[]")]
    string[] Provers { get; set; }

    [ConfigItem(Description = "How long to keep collecting proofs once the threshold is met, in milliseconds, so the same fastest attesters are " +
        "not the only ones ever recorded.", DefaultValue = "250")]
    int AttestationGraceMs { get; set; }

    [ConfigItem(Description = "The L1 account that signs the batches, read from the keystore with `Eez.PosterPasswordFile`. It must not be in " +
        "`KeyStore.UnlockAccounts`, where the node's JSON-RPC signing methods would reach it. Required when `Eez.SequencerEnabled` is set.",
        DefaultValue = "null")]
    string? PosterAddress { get; set; }

    [ConfigItem(Description = "The file holding the keystore password of `Eez.PosterAddress`. Required when `Eez.SequencerEnabled` is set.",
        DefaultValue = "null")]
    string? PosterPasswordFile { get; set; }

    [ConfigItem(Description = "The builder that takes each batch as a bundle pinned to its L1 block. Without one, batches go to the L1 mempool, " +
        "which cannot pin them to their slot.", DefaultValue = "null")]
    string? L1BuilderRpcUrl { get; set; }

    [ConfigItem(Description = "The priority fee of a batch transaction, in wei.", DefaultValue = "10000000000")]
    ulong PostBatchPriorityFee { get; set; }

    [ConfigItem(Description = "The gas limit of a batch transaction. Each block's transactions are kept to the DA one slot's batch fits in, " +
        "and a longer backlog that needs more is settled in chunks ending on the slot grid.", DefaultValue = "16777216")]
    ulong MaxPostBatchGas { get; set; }

    [ConfigItem(Description = "The most L2 blocks one batch settles; a longer backlog is settled in chunks ending on the slot grid. Rounded down " +
        "to whole slots, and at least one.", DefaultValue = "300")]
    ulong MaxBlocksPerBatch { get; set; }

    [ConfigItem(Description = "How many blocks the sequencer may build above what L1 settled before it waits; 0 for no limit.", DefaultValue = "64")]
    ulong MaxSpeculativeDepth { get; set; }

    [ConfigItem(Description = "The beneficiary of the blocks the sequencer produces; the batch's DA carries it. The zero address when not set.",
        DefaultValue = "null")]
    string? SequencerFeeRecipient { get; set; }
}
