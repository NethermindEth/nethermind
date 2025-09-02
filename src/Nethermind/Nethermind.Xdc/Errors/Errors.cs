// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Errors;
public static class CommonErrors
{
    // Various error messages to mark blocks invalid. 
    // These are private in Go, but in C# we typically use public to limit exposure within the assembly.
    public static readonly Exception ErrUnknownBlock =
        new Exception("unknown block");

    public static readonly Exception ErrInvalidCheckpointBeneficiary =
        new Exception("beneficiary in checkpoint block non-zero");

    public static readonly Exception ErrInvalidVote =
        new Exception("vote nonce not 0x00..0 or 0xff..f");

    public static readonly Exception ErrInvalidCheckpointVote =
        new Exception("vote nonce in checkpoint block non-zero");

    public static readonly Exception ErrMissingVanity =
        new Exception("extra-data 32 byte vanity prefix missing");

    public static readonly Exception ErrMissingSignature =
        new Exception("extra-data 65 byte suffix signature missing");

    public static readonly Exception ErrExtraSigners =
        new Exception("non-checkpoint block contains extra signer list");

    public static readonly Exception ErrInvalidCheckpointSigners =
        new Exception("invalid signer list on checkpoint block");

    public static readonly Exception ErrInvalidCheckpointPenalties =
        new Exception("invalid penalty list on checkpoint block");

    public static readonly Exception ErrValidatorsNotLegit =
        new Exception("validators does not match what's stored in snapshot minus its penalty");

    public static readonly Exception ErrPenaltiesNotLegit =
        new Exception("penalties does not match");

    public static readonly Exception ErrInvalidMixDigest =
        new Exception("non-zero mix digest");

    public static readonly Exception ErrInvalidUncleHash =
        new Exception("non empty uncle hash");

    public static readonly Exception ErrInvalidDifficulty =
        new Exception("invalid difficulty");

    public static readonly Exception ErrInvalidTimestamp =
        new Exception("invalid timestamp");

    public static readonly Exception ErrInvalidVotingChain =
        new Exception("invalid voting chain");

    public static readonly Exception ErrInvalidHeaderOrder =
        new Exception("invalid header order");

    public static readonly Exception ErrInvalidChild =
        new Exception("invalid header child");

    public static readonly Exception ErrUnauthorized =
        new Exception("unauthorized");

    public static readonly Exception ErrFailedDoubleValidation =
        new Exception("wrong pair of creator-validator in double validation");

    public static readonly Exception ErrWaitTransactions =
        new Exception("waiting for transactions");

    public static readonly Exception ErrInvalidCheckpointValidators =
        new Exception("invalid validators list on checkpoint block");

    public static readonly Exception ErrEmptyEpochSwitchValidators =
        new Exception("empty validators list on epoch switch block");

    public static readonly Exception ErrInvalidV2Extra =
        new Exception("invalid v2 extra in the block");

    public static readonly Exception ErrInvalidQC =
        new Exception("invalid QC content");

    public static readonly Exception ErrInvalidQCSignatures =
        new Exception("invalid QC Signatures");

    public static readonly Exception ErrInvalidTC =
        new Exception("invalid TC content");

    public static readonly Exception ErrInvalidTCSignatures =
        new Exception("invalid TC Signatures");

    public static readonly Exception ErrEmptyBlockInfoHash =
        new Exception("blockInfo hash is empty");

    public static readonly Exception ErrInvalidFieldInNonEpochSwitch =
        new Exception("invalid field exist in a non-epoch swtich block");

    public static readonly Exception ErrValidatorNotWithinMasternodes =
        new Exception("validator address is not in the master node list");

    public static readonly Exception ErrCoinbaseAndValidatorMismatch =
        new Exception("validator and coinbase address in header does not match");

    public static readonly Exception ErrNotItsTurn =
        new Exception("not validator's turn to mine this block");

    public static readonly Exception ErrRoundInvalid =
        new Exception("invalid Round, it shall be bigger than QC round");

    public static readonly Exception ErrAlreadyMined =
        new Exception("already mined");
}
