// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.ors;
public static class Commonors
{
    // Various or messages to mark blocks invalid. 
    // These are private in Go, but in C# we typically use public to limit exposure within the assembly.
    public static readonly Exception UnknownBlock =
        new Exception("unknown block");

    public static readonly Exception InvalidCheckpointBeneficiary =
        new Exception("beneficiary in checkpoint block non-zero");

    public static readonly Exception InvalidVote =
        new Exception("vote nonce not 0x00..0 or 0xff..f");

    public static readonly Exception InvalidCheckpointVote =
        new Exception("vote nonce in checkpoint block non-zero");

    public static readonly Exception MissingVanity =
        new Exception("extra-data 32 byte vanity prefix missing");

    public static readonly Exception MissingSignature =
        new Exception("extra-data 65 byte suffix signature missing");

    public static readonly Exception ExtraSigners =
        new Exception("non-checkpoint block contains extra signer list");

    public static readonly Exception InvalidCheckpointSigners =
        new Exception("invalid signer list on checkpoint block");

    public static readonly Exception InvalidCheckpointPenalties =
        new Exception("invalid penalty list on checkpoint block");

    public static readonly Exception ValidatorsNotLegit =
        new Exception("validators does not match what's stored in snapshot minus its penalty");

    public static readonly Exception PenaltiesNotLegit =
        new Exception("penalties does not match");

    public static readonly Exception InvalidMixDigest =
        new Exception("non-zero mix digest");

    public static readonly Exception InvalidUncleHash =
        new Exception("non empty uncle hash");

    public static readonly Exception InvalidDifficulty =
        new Exception("invalid difficulty");

    public static readonly Exception InvalidTimestamp =
        new Exception("invalid timestamp");

    public static readonly Exception InvalidVotingChain =
        new Exception("invalid voting chain");

    public static readonly Exception InvalidHeaderOrder =
        new Exception("invalid header order");

    public static readonly Exception InvalidChild =
        new Exception("invalid header child");

    public static readonly Exception Unauthorized =
        new Exception("unauthorized");

    public static readonly Exception FailedDoubleValidation =
        new Exception("wrong pair of creator-validator in double validation");

    public static readonly Exception WaitTransactions =
        new Exception("waiting for transactions");

    public static readonly Exception InvalidCheckpointValidators =
        new Exception("invalid validators list on checkpoint block");

    public static readonly Exception EmptyEpochSwitchValidators =
        new Exception("empty validators list on epoch switch block");

    public static readonly Exception InvalidV2Extra =
        new Exception("invalid v2 extra in the block");

    public static readonly Exception InvalidQC =
        new Exception("invalid QC content");

    public static readonly Exception InvalidQCSignatures =
        new Exception("invalid QC Signatures");

    public static readonly Exception InvalidTC =
        new Exception("invalid TC content");

    public static readonly Exception InvalidTCSignatures =
        new Exception("invalid TC Signatures");

    public static readonly Exception EmptyBlockInfoHash =
        new Exception("blockInfo hash is empty");

    public static readonly Exception InvalidFieldInNonEpochSwitch =
        new Exception("invalid field exist in a non-epoch swtich block");

    public static readonly Exception ValidatorNotWithinMasternodes =
        new Exception("validator address is not in the master node list");

    public static readonly Exception CoinbaseAndValidatorMismatch =
        new Exception("validator and coinbase address in header does not match");

    public static readonly Exception NotItsTurn =
        new Exception("not validator's turn to mine this block");

    public static readonly Exception RoundInvalid =
        new Exception("invalid Round, it shall be bigger than QC round");

    public static readonly Exception AlreadyMined =
        new Exception("already mined");
}
