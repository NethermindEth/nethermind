// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Nethermind.JsonRpc;

internal static class KnownRpcMethodNames
{
    // Order matters inside each bucket: matching probes sequentially after length gating.
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
        "eth_baseFee",
        "eth_getCode",
        "eth_getLogs",
        "eth_syncing",
        "net_version",
        "trace_block",
        "admin_peers",
        "admin_prune",
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
        "eth_newFilter",
        "eth_subscribe",
        "txpool_status",
        "debug_gcStats",
        "debug_setHead",
        "admin_addPeer",
        "admin_dataDir",
        "admin_setSolc",
    ];

    private static readonly string[] Length14 =
    [
        "eth_feeHistory",
        "eth_getAccount",
        "eth_getBalance",
        "eth_simulateV1",
        "txpool_content",
        "txpool_inspect",
        "trace_callMany",
        "debug_memStats",
        "debug_seedHash",
        "admin_nodeInfo",
    ];

    private static readonly string[] Length15 =
    [
        "eth_blobBaseFee",
        "eth_blockNumber",
        "eth_estimateGas",
        "eth_unsubscribe",
        "debug_traceCall",
        "debug_resetHead",
        "debug_dumpBlock",
        "debug_getFromDb",
        "admin_subscribe",
    ];

    private static readonly string[] Length16 =
    [
        "eth_getStorageAt",
        "eth_subscription",
        "trace_simulateV1",
        "debug_traceBlock",
        "debug_simulateV1",
        "admin_removePeer",
        "admin_verifyTrie",
    ];

    private static readonly string[] Length17 =
    [
        "engine_getBlobsV2",
        "engine_getBlobsV3",
        "engine_getBlobsV4",
        "engine_getBlobsV1",
        "eth_getFilterLogs",
        "trace_transaction",
        "debug_getRawBlock",
        "debug_getBlockRlp",
        "admin_unsubscribe",
    ];

    private static readonly string[] Length18 =
    [
        "eth_getAccountInfo",
        "eth_getBlockByHash",
        "eth_newBlockFilter",
        "txpool_contentFrom",
        "debug_getBadBlocks",
        "debug_getRawHeader",
        "debug_getSyncStage",
        "admin_subscription",
    ];

    private static readonly string[] Length19 =
    [
        "engine_getPayloadV5",
        "engine_getPayloadV6",
        "engine_newPayloadV5",
        "engine_newPayloadV4",
        "engine_getPayloadV4",
        "engine_newPayloadV3",
        "engine_getPayloadV3",
        "engine_newPayloadV2",
        "engine_getPayloadV2",
        "engine_newPayloadV1",
        "engine_getPayloadV1",
        "eth_getHeaderByHash",
        "eth_protocolVersion",
        "eth_sendTransaction",
        "eth_signTransaction",
        "eth_uninstallFilter",
        "rbuilder_getAccount",
        "debug_traceCallMany",
        "debug_getChainLevel",
        "admin_exportHistory",
        "admin_importHistory",
    ];

    private static readonly string[] Length20 =
    [
        "eth_getBlockByNumber",
        "eth_createAccessList",
        "eth_getBlockReceipts",
        "eth_getFilterChanges",
        "eth_getStorageValues",
        "testing_buildBlockV1",
        "trace_rawTransaction",
        "debug_getRawReceipts",
        "debug_getConfigValue",
        "debug_insertReceipts",
        "admin_addTrustedPeer",
    ];

    private static readonly string[] Length21 =
    [
        "eth_getHeaderByNumber",
        "rbuilder_getBlockHash",
        "testing_commitBlockV1",
        "debug_migrateReceipts",
    ];

    private static readonly string[] Length22 =
    [
        "eth_getBlockAccessList",
        "eth_sendRawTransaction",
        "rbuilder_getCodeByHash",
        "debug_traceTransaction",
        "debug_traceBlockByHash",
        "debug_executionWitness",
        "debug_deleteChainSlice",
        "admin_exportEraHistory",
        "admin_importEraHistory",
    ];

    private static readonly string[] Length23 =
    [
        "eth_getTransactionCount",
        "eth_pendingTransactions",
        "trace_replayTransaction",
        "debug_getRawTransaction",
        "debug_intermediateRoots",
        "debug_getBlockRlpByHash",
        "admin_removeTrustedPeer",
    ];

    private static readonly string[] Length24 =
    [
        "eth_getTransactionByHash",
        "eth_maxPriorityFeePerGas",
        "debug_traceBlockByNumber",
        "debug_traceBlockFromFile",
    ];

    private static readonly string[] Length25 =
    [
        "engine_getClientVersionV1",
        "eth_getTransactionReceipt",
    ];

    private static readonly string[] Length26 =
    [
        "engine_forkchoiceUpdatedV4",
        "engine_forkchoiceUpdatedV3",
        "engine_forkchoiceUpdatedV2",
        "engine_forkchoiceUpdatedV1",
        "eth_sendRawTransactionSync",
        "proof_getTransactionByHash",
        "debug_executionWitnessCall",
        "admin_isStateRootAvailable",
    ];

    private static readonly string[] Length27 =
    [
        "engine_exchangeCapabilities",
        "eth_getRawTransactionByHash",
        "proof_getTransactionReceipt",
        "rbuilder_calculateStateRoot",
        "debug_getRawBlockAccessList",
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
        "eth_getBlockAccessListByNumber",
        "eth_getUncleCountByBlockNumber",
        "debug_standardTraceBlockToFile",
    ];

    private static readonly string[] Length31 =
    [
        "engine_getPayloadBodiesByHashV2",
        "engine_getPayloadBodiesByHashV1",
        "eth_getUncleByBlockHashAndIndex",
        "eth_newPendingTransactionFilter",
    ];

    private static readonly string[] Length32 =
    [
        "engine_getPayloadBodiesByRangeV2",
        "engine_getPayloadBodiesByRangeV1",
    ];

    private static readonly string[] Length33 =
    [
        "eth_getUncleByBlockNumberAndIndex",
        "debug_standardTraceBadBlockToFile",
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
        "eth_getBlockTransactionCountByNumber",
        "debug_traceTransactionInBlockByIndex",
    ];

    private static readonly string[] Length37 =
    [
        "eth_getTransactionByBlockHashAndIndex",
        "debug_traceTransactionByBlockAndIndex",
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

    private static readonly byte[][] Length8Utf8 = CreateUtf8MethodNames(Length8);
    private static readonly byte[][] Length9Utf8 = CreateUtf8MethodNames(Length9);
    private static readonly byte[][] Length10Utf8 = CreateUtf8MethodNames(Length10);
    private static readonly byte[][] Length11Utf8 = CreateUtf8MethodNames(Length11);
    private static readonly byte[][] Length12Utf8 = CreateUtf8MethodNames(Length12);
    private static readonly byte[][] Length13Utf8 = CreateUtf8MethodNames(Length13);
    private static readonly byte[][] Length14Utf8 = CreateUtf8MethodNames(Length14);
    private static readonly byte[][] Length15Utf8 = CreateUtf8MethodNames(Length15);
    private static readonly byte[][] Length16Utf8 = CreateUtf8MethodNames(Length16);
    private static readonly byte[][] Length17Utf8 = CreateUtf8MethodNames(Length17);
    private static readonly byte[][] Length18Utf8 = CreateUtf8MethodNames(Length18);
    private static readonly byte[][] Length19Utf8 = CreateUtf8MethodNames(Length19);
    private static readonly byte[][] Length20Utf8 = CreateUtf8MethodNames(Length20);
    private static readonly byte[][] Length21Utf8 = CreateUtf8MethodNames(Length21);
    private static readonly byte[][] Length22Utf8 = CreateUtf8MethodNames(Length22);
    private static readonly byte[][] Length23Utf8 = CreateUtf8MethodNames(Length23);
    private static readonly byte[][] Length24Utf8 = CreateUtf8MethodNames(Length24);
    private static readonly byte[][] Length25Utf8 = CreateUtf8MethodNames(Length25);
    private static readonly byte[][] Length26Utf8 = CreateUtf8MethodNames(Length26);
    private static readonly byte[][] Length27Utf8 = CreateUtf8MethodNames(Length27);
    private static readonly byte[][] Length28Utf8 = CreateUtf8MethodNames(Length28);
    private static readonly byte[][] Length29Utf8 = CreateUtf8MethodNames(Length29);
    private static readonly byte[][] Length30Utf8 = CreateUtf8MethodNames(Length30);
    private static readonly byte[][] Length31Utf8 = CreateUtf8MethodNames(Length31);
    private static readonly byte[][] Length32Utf8 = CreateUtf8MethodNames(Length32);
    private static readonly byte[][] Length33Utf8 = CreateUtf8MethodNames(Length33);
    private static readonly byte[][] Length34Utf8 = CreateUtf8MethodNames(Length34);
    private static readonly byte[][] Length35Utf8 = CreateUtf8MethodNames(Length35);
    private static readonly byte[][] Length36Utf8 = CreateUtf8MethodNames(Length36);
    private static readonly byte[][] Length37Utf8 = CreateUtf8MethodNames(Length37);
    private static readonly byte[][] Length39Utf8 = CreateUtf8MethodNames(Length39);
    private static readonly byte[][] Length40Utf8 = CreateUtf8MethodNames(Length40);
    private static readonly byte[][] Length41Utf8 = CreateUtf8MethodNames(Length41);
    private static readonly byte[][] Length42Utf8 = CreateUtf8MethodNames(Length42);

    public static IReadOnlyList<string> All => s_all;

    public static string? Intern(ref Utf8JsonReader methodReader)
    {
        int methodLength = methodReader.HasValueSequence
            ? checked((int)methodReader.ValueSequence.Length)
            : methodReader.ValueSpan.Length;

        string? methodName = methodLength switch
        {
            8 => Match(ref methodReader, Length8, Length8Utf8),
            9 => Match(ref methodReader, Length9, Length9Utf8),
            10 => Match(ref methodReader, Length10, Length10Utf8),
            11 => Match(ref methodReader, Length11, Length11Utf8),
            12 => Match(ref methodReader, Length12, Length12Utf8),
            13 => Match(ref methodReader, Length13, Length13Utf8),
            14 => Match(ref methodReader, Length14, Length14Utf8),
            15 => Match(ref methodReader, Length15, Length15Utf8),
            16 => Match(ref methodReader, Length16, Length16Utf8),
            17 => Match(ref methodReader, Length17, Length17Utf8),
            18 => Match(ref methodReader, Length18, Length18Utf8),
            19 => Match(ref methodReader, Length19, Length19Utf8),
            20 => Match(ref methodReader, Length20, Length20Utf8),
            21 => Match(ref methodReader, Length21, Length21Utf8),
            22 => Match(ref methodReader, Length22, Length22Utf8),
            23 => Match(ref methodReader, Length23, Length23Utf8),
            24 => Match(ref methodReader, Length24, Length24Utf8),
            25 => Match(ref methodReader, Length25, Length25Utf8),
            26 => Match(ref methodReader, Length26, Length26Utf8),
            27 => Match(ref methodReader, Length27, Length27Utf8),
            28 => Match(ref methodReader, Length28, Length28Utf8),
            29 => Match(ref methodReader, Length29, Length29Utf8),
            30 => Match(ref methodReader, Length30, Length30Utf8),
            31 => Match(ref methodReader, Length31, Length31Utf8),
            32 => Match(ref methodReader, Length32, Length32Utf8),
            33 => Match(ref methodReader, Length33, Length33Utf8),
            34 => Match(ref methodReader, Length34, Length34Utf8),
            35 => Match(ref methodReader, Length35, Length35Utf8),
            36 => Match(ref methodReader, Length36, Length36Utf8),
            37 => Match(ref methodReader, Length37, Length37Utf8),
            39 => Match(ref methodReader, Length39, Length39Utf8),
            40 => Match(ref methodReader, Length40, Length40Utf8),
            41 => Match(ref methodReader, Length41, Length41Utf8),
            42 => Match(ref methodReader, Length42, Length42Utf8),
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

    private static string? Match(ref Utf8JsonReader methodReader, string[] methodNames, byte[][] utf8MethodNames)
    {
        for (int i = 0; i < methodNames.Length; i++)
        {
            if (methodReader.ValueTextEquals(utf8MethodNames[i]))
            {
                return methodNames[i];
            }
        }

        return null;
    }

    private static byte[][] CreateUtf8MethodNames(string[] methodNames)
    {
        byte[][] utf8MethodNames = new byte[methodNames.Length][];
        for (int i = 0; i < methodNames.Length; i++)
        {
            utf8MethodNames[i] = Encoding.UTF8.GetBytes(methodNames[i]);
        }

        return utf8MethodNames;
    }
}
