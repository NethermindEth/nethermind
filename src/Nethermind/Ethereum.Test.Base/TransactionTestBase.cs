// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;

namespace Ethereum.Test.Base;

public abstract class TransactionTestBase
{
    private static readonly TxValidator s_mainnetTxValidator = new(MainnetSpecProvider.Instance.ChainId);

    protected static Result RunTest(TransactionTest test)
    {
        if (string.IsNullOrEmpty(test.TxBytes))
        {
            return Result.Fail("Missing txbytes");
        }
        if (string.IsNullOrEmpty(test.Fork))
        {
            return Result.Fail("Missing fork");
        }

        IReleaseSpec spec;
        try
        {
            spec = SpecNameParser.Parse(test.Fork);
        }
        catch (Exception ex)
        {
            return Result.Fail($"Unknown fork '{test.Fork}': {ex.Message}");
        }

        bool decoded = TryDecode(test.TxBytes, out Transaction? tx, out string? decodeError);
        string? observedError = decoded ? s_mainnetTxValidator.IsWellFormed(tx!, spec).Error : decodeError;

        bool expectFailure = !string.IsNullOrEmpty(test.ExpectedException);
        if (expectFailure)
        {
            if (observedError is null)
            {
                return Result.Fail($"Expected exception '{test.ExpectedException}' but tx validated cleanly.");
            }
            if (!ExceptionMatches(test.ExpectedException!, observedError))
            {
                return Result.Fail($"Expected '{test.ExpectedException}' but got '{observedError}'.");
            }
        }
        else if (observedError is not null)
        {
            return Result.Fail($"Expected success but got error '{observedError}'.");
        }

        return Result.Success;
    }

    private static bool TryDecode(string txBytesHex, out Transaction? tx, out string? error)
    {
        tx = null;
        error = null;
        try
        {
            byte[] bytes = Bytes.FromHexString(txBytesHex);
            tx = Rlp.Decode<Transaction>(bytes, RlpBehaviors.SkipTypedWrapping);
            return tx is not null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool ExceptionMatches(string expected, string observed)
    {
        // EEST tokens may pipe-OR multiple acceptable failure modes (e.g. signature
        // checks that overlap). Accept a match if any alternative substrings into the
        // observed Nethermind error or fixture token.
        foreach (string token in expected.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (s_exceptionToErrorFragments.TryGetValue(token, out string[]? fragments))
            {
                foreach (string fragment in fragments)
                {
                    if (observed.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            // Also accept if the token itself is contained verbatim (fallback for fixtures
            // that ship Nethermind-style messages) or contains the observed error.
            if (observed.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // Fixture token → fragments we look for in the Nethermind-side ValidationResult.Error
    // or the RLP decode exception message. Mirrors BlockchainTestBase's mapping; multiple
    // fragments cover RLP-level rejects that surface before the validator runs. Both the
    // INVALID_AUTHORIZATION_FORMAT and INVALID_AUTHORITY_SIGNATURE tokens admit any RLP
    // structural failure since the fixtures' "format" vs "signature" partition rests on
    // intent rather than where decoding fails first.
    private static readonly string[] s_rlpDecodeFragments =
    [
        "Non-canonical integer",
        "RLP",
        "Could not decode",
        "Unexpected number of items",
        "Unexpected byte value",
        "Data checkpoint failed",
        "Collection count",
        "sequence prefix",
        "expected length",
        "Invalid signature",
    ];

    private static readonly System.Collections.Generic.Dictionary<string, string[]> s_exceptionToErrorFragments = new()
    {
        ["TransactionException.TYPE_4_EMPTY_AUTHORIZATION_LIST"] = ["EIP-7702 transaction with empty auth list"],
        ["TransactionException.TYPE_4_INVALID_AUTHORIZATION_FORMAT"] = ["InvalidAuthorityList", .. s_rlpDecodeFragments],
        ["TransactionException.TYPE_4_INVALID_AUTHORITY_SIGNATURE"] = ["InvalidAuthoritySignature", .. s_rlpDecodeFragments],
        ["TransactionException.TYPE_4_INVALID_AUTHORITY_SIGNATURE_S_TOO_HIGH"] = ["InvalidAuthoritySignature", "S value too high", .. s_rlpDecodeFragments],
        ["TransactionException.TYPE_4_TX_CONTRACT_CREATION"] = ["EIP-7702 transaction cannot be used to create contract"],
        ["TransactionException.TYPE_4_TX_PRE_FORK"] = ["InvalidTxType"],
        ["TransactionException.TYPE_3_TX_PRE_FORK"] = ["InvalidTxType"],
        ["TransactionException.TYPE_2_TX_PRE_FORK"] = ["InvalidTxType"],
        ["TransactionException.TYPE_1_TX_PRE_FORK"] = ["InvalidTxType"],
        ["TransactionException.TYPE_3_TX_ZERO_BLOBS"] = ["blob transaction must have at least 1 blob"],
        ["TransactionException.TYPE_3_TX_INVALID_BLOB_VERSIONED_HASH"] = ["InvalidBlobVersionedHashVersion"],
        ["TransactionException.TYPE_3_TX_CONTRACT_CREATION"] = ["blob transaction of type create"],
        ["TransactionException.INSUFFICIENT_MAX_FEE_PER_BLOB_GAS"] = ["max fee per blob gas less than block blob gas fee"],
    };
}

