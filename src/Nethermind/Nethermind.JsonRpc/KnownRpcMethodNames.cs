// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;

namespace Nethermind.JsonRpc;

internal static class KnownRpcMethodNames
{
    private static readonly string[] Length8 =
    [
        "eth_call",
        "eth_sign",
    ];

    private static readonly string[] Length9 =
    [
        "trace_get",
    ];

    private static readonly string[] Length10 =
    [
        "eth_config",
        "proof_call",
        "trace_call",
    ];

    private static readonly string[] Length11 =
    [
        "eth_chainId",
        "admin_peers",
        "admin_prune",
        "eth_baseFee",
        "eth_getCode",
        "eth_getLogs",
        "eth_syncing",
        "net_version",
        "trace_block",
    ];

    private static readonly string[] Length12 =
    [
        "eth_accounts",
        "eth_coinbase",
        "eth_gasPrice",
        "eth_getProof",
        "eth_snapshot",
        "trace_filter",
    ];

    private static readonly string[] Length13 =
    [
        "admin_addPeer",
        "admin_dataDir",
        "admin_setSolc",
        "debug_gcStats",
        "debug_setHead",
        "eth_newFilter",
        "eth_subscribe",
        "txpool_status",
    ];

    private static readonly string[] Length14 =
    [
        "admin_nodeInfo",
        "debug_memStats",
        "debug_seedHash",
        "eth_feeHistory",
        "eth_getAccount",
        "eth_getBalance",
        "eth_simulateV1",
        "trace_callMany",
        "txpool_content",
        "txpool_inspect",
    ];

    private static readonly string[] Length15 =
    [
        "admin_subscribe",
        "debug_resetHead",
        "debug_traceCall",
        "debug_dumpBlock",
        "debug_getFromDb",
        "eth_blobBaseFee",
        "eth_blockNumber",
        "eth_estimateGas",
        "eth_unsubscribe",
    ];

    private static readonly string[] Length16 =
    [
        "admin_removePeer",
        "admin_verifyTrie",
        "debug_traceBlock",
        "debug_simulateV1",
        "eth_getStorageAt",
        "eth_subscription",
        "trace_simulateV1",
    ];

    private static readonly string[] Length17 =
    [
        "engine_getBlobsV2",
        "admin_unsubscribe",
        "debug_getRawBlock",
        "debug_getBlockRlp",
        "engine_getBlobsV1",
        "engine_getBlobsV3",
        "engine_getBlobsV4",
        "eth_getFilterLogs",
        "trace_transaction",
    ];

    private static readonly string[] Length18 =
    [
        "admin_subscription",
        "debug_getBadBlocks",
        "debug_getRawHeader",
        "debug_getSyncStage",
        "eth_getAccountInfo",
        "eth_getBlockByHash",
        "eth_newBlockFilter",
        "txpool_contentFrom",
    ];

    private static readonly string[] Length19 =
    [
        "engine_newPayloadV4",
        "admin_exportHistory",
        "admin_importHistory",
        "debug_getChainLevel",
        "debug_traceCallMany",
        "engine_getPayloadV1",
        "engine_getPayloadV2",
        "engine_getPayloadV3",
        "engine_getPayloadV4",
        "engine_getPayloadV5",
        "engine_getPayloadV6",
        "engine_newPayloadV1",
        "engine_newPayloadV2",
        "engine_newPayloadV3",
        "engine_newPayloadV5",
        "eth_getHeaderByHash",
        "eth_protocolVersion",
        "eth_sendTransaction",
        "eth_signTransaction",
        "eth_uninstallFilter",
        "rbuilder_getAccount",
    ];

    private static readonly string[] Length20 =
    [
        "eth_getBlockByNumber",
        "admin_addTrustedPeer",
        "debug_getRawReceipts",
        "debug_getConfigValue",
        "debug_insertReceipts",
        "eth_createAccessList",
        "eth_getBlockReceipts",
        "eth_getFilterChanges",
        "eth_getStorageValues",
        "testing_buildBlockV1",
        "trace_rawTransaction",
    ];

    private static readonly string[] Length21 =
    [
        "debug_migrateReceipts",
        "eth_getHeaderByNumber",
        "rbuilder_getBlockHash",
        "testing_commitBlockV1",
    ];

    private static readonly string[] Length22 =
    [
        "admin_exportEraHistory",
        "admin_importEraHistory",
        "debug_deleteChainSlice",
        "debug_traceTransaction",
        "debug_traceBlockByHash",
        "debug_executionWitness",
        "eth_getBlockAccessList",
        "eth_sendRawTransaction",
        "rbuilder_getCodeByHash",
    ];

    private static readonly string[] Length23 =
    [
        "admin_removeTrustedPeer",
        "debug_getRawTransaction",
        "debug_intermediateRoots",
        "debug_getBlockRlpByHash",
        "eth_getTransactionCount",
        "eth_pendingTransactions",
        "trace_replayTransaction",
    ];

    private static readonly string[] Length24 =
    [
        "debug_traceBlockByNumber",
        "debug_traceBlockFromFile",
        "eth_getTransactionByHash",
        "eth_maxPriorityFeePerGas",
    ];

    private static readonly string[] Length25 =
    [
        "engine_getClientVersionV1",
        "eth_getTransactionReceipt",
    ];

    private static readonly string[] Length26 =
    [
        "engine_forkchoiceUpdatedV3",
        "admin_isStateRootAvailable",
        "debug_executionWitnessCall",
        "engine_forkchoiceUpdatedV1",
        "engine_forkchoiceUpdatedV2",
        "engine_forkchoiceUpdatedV4",
        "eth_sendRawTransactionSync",
        "proof_getTransactionByHash",
    ];

    private static readonly string[] Length27 =
    [
        "debug_getRawBlockAccessList",
        "engine_exchangeCapabilities",
        "eth_getRawTransactionByHash",
        "proof_getTransactionReceipt",
        "rbuilder_calculateStateRoot",
    ];

    private static readonly string[] Length28 =
    [
        "eth_getBlockAccessListByHash",
        "eth_getUncleCountByBlockHash",
    ];

    private static readonly string[] Length29 =
    [
        "trace_replayBlockTransactions",
    ];

    private static readonly string[] Length30 =
    [
        "debug_standardTraceBlockToFile",
        "eth_getBlockAccessListByNumber",
        "eth_getUncleCountByBlockNumber",
    ];

    private static readonly string[] Length31 =
    [
        "engine_getPayloadBodiesByHashV1",
        "engine_getPayloadBodiesByHashV2",
        "eth_getUncleByBlockHashAndIndex",
        "eth_newPendingTransactionFilter",
    ];

    private static readonly string[] Length32 =
    [
        "engine_getPayloadBodiesByRangeV1",
        "engine_getPayloadBodiesByRangeV2",
    ];

    private static readonly string[] Length33 =
    [
        "debug_standardTraceBadBlockToFile",
        "eth_getUncleByBlockNumberAndIndex",
    ];

    private static readonly string[] Length34 =
    [
        "eth_getBlockTransactionCountByHash",
    ];

    private static readonly string[] Length35 =
    [
        "debug_traceTransactionInBlockByHash",
    ];

    private static readonly string[] Length36 =
    [
        "debug_traceTransactionInBlockByIndex",
        "eth_getBlockTransactionCountByNumber",
    ];

    private static readonly string[] Length37 =
    [
        "debug_traceTransactionByBlockAndIndex",
        "eth_getTransactionByBlockHashAndIndex",
    ];

    private static readonly string[] Length39 =
    [
        "eth_getTransactionByBlockNumberAndIndex",
    ];

    private static readonly string[] Length40 =
    [
        "engine_exchangeTransitionConfigurationV1",
        "eth_getRawTransactionByBlockHashAndIndex",
    ];

    private static readonly string[] Length41 =
    [
        "debug_traceTransactionByBlockhashAndIndex",
    ];

    private static readonly string[] Length42 =
    [
        "eth_getRawTransactionByBlockNumberAndIndex",
    ];

    private static readonly string[] s_all =
    [
        .. Length8,
        .. Length9,
        .. Length10,
        .. Length11,
        .. Length12,
        .. Length13,
        .. Length14,
        .. Length15,
        .. Length16,
        .. Length17,
        .. Length18,
        .. Length19,
        .. Length20,
        .. Length21,
        .. Length22,
        .. Length23,
        .. Length24,
        .. Length25,
        .. Length26,
        .. Length27,
        .. Length28,
        .. Length29,
        .. Length30,
        .. Length31,
        .. Length32,
        .. Length33,
        .. Length34,
        .. Length35,
        .. Length36,
        .. Length37,
        .. Length39,
        .. Length40,
        .. Length41,
        .. Length42,
    ];

    public static IReadOnlyList<string> All => s_all;

    public static string? Intern(ref Utf8JsonReader methodReader)
    {
        string? methodName = methodReader.ValueSpan.Length switch
        {
            8 => Match(ref methodReader, Length8),
            9 => Match(ref methodReader, Length9),
            10 => Match(ref methodReader, Length10),
            11 => Match(ref methodReader, Length11),
            12 => Match(ref methodReader, Length12),
            13 => Match(ref methodReader, Length13),
            14 => Match(ref methodReader, Length14),
            15 => Match(ref methodReader, Length15),
            16 => Match(ref methodReader, Length16),
            17 => Match(ref methodReader, Length17),
            18 => Match(ref methodReader, Length18),
            19 => Match(ref methodReader, Length19),
            20 => Match(ref methodReader, Length20),
            21 => Match(ref methodReader, Length21),
            22 => Match(ref methodReader, Length22),
            23 => Match(ref methodReader, Length23),
            24 => Match(ref methodReader, Length24),
            25 => Match(ref methodReader, Length25),
            26 => Match(ref methodReader, Length26),
            27 => Match(ref methodReader, Length27),
            28 => Match(ref methodReader, Length28),
            29 => Match(ref methodReader, Length29),
            30 => Match(ref methodReader, Length30),
            31 => Match(ref methodReader, Length31),
            32 => Match(ref methodReader, Length32),
            33 => Match(ref methodReader, Length33),
            34 => Match(ref methodReader, Length34),
            35 => Match(ref methodReader, Length35),
            36 => Match(ref methodReader, Length36),
            37 => Match(ref methodReader, Length37),
            39 => Match(ref methodReader, Length39),
            40 => Match(ref methodReader, Length40),
            41 => Match(ref methodReader, Length41),
            42 => Match(ref methodReader, Length42),
            _ => null
        };

        return methodName ?? methodReader.GetString();
    }

    public static string? Intern(JsonElement methodElement)
    {
        string[] methods = s_all;
        for (int i = 0; i < methods.Length; i++)
        {
            string methodName = methods[i];
            if (methodElement.ValueEquals(methodName))
            {
                return methodName;
            }
        }

        return methodElement.GetString();
    }

    private static string? Match(ref Utf8JsonReader methodReader, string[] methodNames)
    {
        for (int i = 0; i < methodNames.Length; i++)
        {
            string methodName = methodNames[i];
            if (methodReader.ValueTextEquals(methodName))
            {
                return methodName;
            }
        }

        return null;
    }
}
