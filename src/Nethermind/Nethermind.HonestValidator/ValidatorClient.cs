//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.HonestValidator.Services;
using Nethermind.Logging.Microsoft;

namespace Nethermind.HonestValidator
{
    public class ValidatorClient
    {
        private readonly BeaconChainInformation _beaconChainInformation;
        private readonly IBeaconNodeApi _beaconNodeApi;
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<SignatureDomains> _signatureDomainOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;
        private readonly IValidatorKeyProvider _validatorKeyProvider;
        private readonly ValidatorState _validatorState;

        public ValidatorClient(ILogger<ValidatorClient> logger,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions,
            IOptionsMonitor<SignatureDomains> signatureDomainOptions,
            ICryptographyService cryptographyService,
            IBeaconNodeApi beaconNodeApi,
            IValidatorKeyProvider validatorKeyProvider,
            BeaconChainInformation beaconChainInformation)
        {
            _logger = logger;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
            _signatureDomainOptions = signatureDomainOptions;
            _cryptographyService = cryptographyService;
            _beaconNodeApi = beaconNodeApi;
            _validatorKeyProvider = validatorKeyProvider;
            _beaconChainInformation = beaconChainInformation;

            _validatorState = new ValidatorState();
        }

        /// <summary>
        /// Returns the domain for the 'domain_type' and 'fork_version'
        /// </summary>
        public Domain ComputeDomain(DomainType domainType, ForkVersion? forkVersion)
        {
            // TODO: Duplicate of BeaconChainUtility

            if (forkVersion == null)
            {
                forkVersion = _initialValueOptions.CurrentValue.GenesisForkVersion;
            }

            Span<byte> combined = stackalloc byte[Domain.Length];
            domainType.AsSpan().CopyTo(combined);
            forkVersion.Value.AsSpan().CopyTo(combined.Slice(DomainType.Length));
            return new Domain(combined);
        }

//        public async Task ProcessProposerDutiesAsync(ulong time)
//        {
//            throw new System.NotSupportedException();
//        }

        /// <summary>
        /// Return the epoch number of ``slot``.
        /// </summary>
        public Epoch ComputeEpochAtSlot(Slot slot)
        {
            // TODO: Duplicate of BeaconChainUtility

            return new Epoch(slot / _timeParameterOptions.CurrentValue.SlotsPerEpoch);
        }

        /// <summary>
        /// Return the signing root of an object by calculating the root of the object-domain tree.
        /// </summary>
        public Root ComputeSigningRoot(Root objectRoot, Domain domain)
        {
            // TODO: Duplicate of BeaconChainUtility

            SigningRoot domainWrappedObject = new SigningRoot(objectRoot, domain);
            return _cryptographyService.HashTreeRoot(domainWrappedObject);
        }

        public BlsSignature GetBlockSignature(BeaconBlock block, BlsPublicKey blsPublicKey)
        {
            var fork = _beaconChainInformation.Fork;
            var epoch = ComputeEpochAtSlot(block.Slot);

            ForkVersion forkVersion;
            if (epoch < fork.Epoch)
            {
                forkVersion = fork.PreviousVersion;
            }
            else
            {
                forkVersion = fork.CurrentVersion;
            }

            var domainType = _signatureDomainOptions.CurrentValue.BeaconProposer;
            var proposerDomain = ComputeDomain(domainType, forkVersion);

            /*
            JsonSerializerOptions options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            options.ConfigureNethermindCore2();
            string blockJson = System.Text.Json.JsonSerializer.Serialize(block, options);
            */

            var blockRoot = _cryptographyService.HashTreeRoot(block);
            var signingRoot = ComputeSigningRoot(blockRoot, proposerDomain);

            var signature = _validatorKeyProvider.SignRoot(blsPublicKey, signingRoot);

            return signature;
        }

        public Slot GetCurrentSlot(BeaconChainInformation beaconChainInformation)
        {
            ulong slotValue = (beaconChainInformation.Time - beaconChainInformation.GenesisTime) /
                              _timeParameterOptions.CurrentValue.SecondsPerSlot;
            return new Slot(slotValue);
        }

        public BlsSignature GetEpochSignature(Slot slot, BlsPublicKey blsPublicKey)
        {
            var fork = _beaconChainInformation.Fork;
            var epoch = ComputeEpochAtSlot(slot);

            ForkVersion forkVersion;
            if (epoch < fork.Epoch)
            {
                forkVersion = fork.PreviousVersion;
            }
            else
            {
                forkVersion = fork.CurrentVersion;
            }

            var domainType = _signatureDomainOptions.CurrentValue.Randao;
            var randaoDomain = ComputeDomain(domainType, forkVersion);

            var epochRoot = _cryptographyService.HashTreeRoot(epoch);
            var signingRoot = ComputeSigningRoot(epochRoot, randaoDomain);

            var randaoReveal = _validatorKeyProvider.SignRoot(blsPublicKey, signingRoot);

            return randaoReveal;
        }

        public async Task OnTickAsync(BeaconChainInformation beaconChainInformation, ulong time,
            CancellationToken cancellationToken)
        {
            // update time
            await beaconChainInformation.SetTimeAsync(time).ConfigureAwait(false);
            Slot currentSlot = GetCurrentSlot(beaconChainInformation);

            // TODO: attestation is done 1/3 way through slot

            // Not a new slot (clock is still the same as the slot we just checked), return
            bool shouldCheckSlot = currentSlot > beaconChainInformation.LastSlotChecked;
            if (!shouldCheckSlot)
            {
                return;
            }

            await UpdateForkVersionActivityAsync(cancellationToken).ConfigureAwait(false);
            
            // TODO: For beacon nodes that don't report SyncingStatus (which is nullable/optional),
            // then need a different strategy to determine the current head known by the beacon node.
            // The current code (2020-03-15) will simply start from slot 0 and process 1/second until
            // caught up with clock slot; at 6 seconds/slot, this is 6x faster, i.e. 1 day still takes 4 hours.
            // (okay for testing).
            // Alternative 1: see if the node supports /beacon/head, and use that slot
            // Alternative 2: try UpdateDuties, and if you get 406 DutiesNotAvailableForRequestedEpoch then use a
            // divide & conquer algorithm to determine block to check. (Could be a back off x1, x2, x4, x8, etc if 406)
            
            await UpdateSyncStatusActivityAsync(cancellationToken).ConfigureAwait(false);

            // Need to have an anchor block (will provide the genesis time) before can do anything
            // Absolutely no point in generating blocks < anchor slot
            // Irrespective of clock time, can't check duties >= 2 epochs ahead of current (head), as don't have data
            //  - actually, validator only cares about what they need to sign this slot
            //  - the beacon node takes care of subscribing to topics an epoch in advance, etc
            // While signing a block < highest (seen) slot may be a waste, there is no penalty for doing so

            // Generally no point in generating blocks <= current (head) slot
            Slot slotToCheck =
                Slot.Max(beaconChainInformation.LastSlotChecked, beaconChainInformation.SyncStatus.CurrentSlot) +
                Slot.One;

            LogDebug.ProcessingSlot(_logger, slotToCheck, currentSlot, beaconChainInformation.SyncStatus.CurrentSlot,
                null);

            // Slot is set before processing; if there is an error (in process; update duties has a try/catch), it will skip to the next slot
            // (maybe the error will be resolved; trade off of whether error can be fixed by retrying, e.g. network error,
            // but potentially getting stuck in a slot, vs missing a slot)
            // TODO: Maybe add a retry policy/retry count when to advance last slot checked regardless
            await beaconChainInformation.SetLastSlotChecked(slotToCheck);

            // TODO: Attestations should run checks one epoch ahead, for topic subscriptions, although this is more a beacon node thing to do.

            // Note that UpdateDuties will continue even if there is an error/issue (i.e. assume no change and process what we have)
            Epoch epochToCheck = ComputeEpochAtSlot(slotToCheck);
            await UpdateDutiesActivityAsync(epochToCheck, cancellationToken).ConfigureAwait(false);

            await ProcessProposalDutiesAsync(slotToCheck, cancellationToken).ConfigureAwait(false);

            // If upcoming attester, join (or change) topics
            // Subscribe to topics

            // Attest 1/3 way through slot
        }

        public async Task ProcessProposalDutiesAsync(Slot slot, CancellationToken cancellationToken)
        {
            // If proposer, get block, sign block, return to node
            // Retry if not successful; need to queue this up to send immediately if connection issue. (or broadcast?)

            BlsPublicKey? blsPublicKey = _validatorState.GetProposalDutyForSlot(slot);
            if (!(blsPublicKey is null))
            {
                Activity activity = new Activity("process-proposal-duty");
                activity.Start();
                using var activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                    activity.TraceId, activity.SpanId);
                try
                {

                    if (_logger.IsInfo())
                        Log.ProposalDutyFor(_logger, slot, _beaconChainInformation.Time, blsPublicKey, null);

                    BlsSignature randaoReveal = GetEpochSignature(slot, blsPublicKey);

                    if (_logger.IsDebug())
                        LogDebug.RequestingBlock(_logger, slot, blsPublicKey.ToShortString(),
                            randaoReveal.ToString().Substring(0, 10), null);

                    ApiResponse<BeaconBlock> newBlockResponse = await _beaconNodeApi
                        .NewBlockAsync(slot, randaoReveal, cancellationToken).ConfigureAwait(false);
                    if (newBlockResponse.StatusCode == StatusCode.Success)
                    {
                        BeaconBlock unsignedBlock = newBlockResponse.Content;
                        BlsSignature blockSignature = GetBlockSignature(unsignedBlock, blsPublicKey);
                        SignedBeaconBlock signedBlock = new SignedBeaconBlock(unsignedBlock, blockSignature);

                        if (_logger.IsDebug())
                            LogDebug.PublishingSignedBlock(_logger, slot, blsPublicKey.ToShortString(),
                                randaoReveal.ToString().Substring(0, 10), signedBlock.Message,
                                signedBlock.Signature.ToString().Substring(0, 10), null);

                        ApiResponse publishBlockResponse = await _beaconNodeApi
                            .PublishBlockAsync(signedBlock, cancellationToken)
                            .ConfigureAwait(false);
                        if (publishBlockResponse.StatusCode != StatusCode.Success && publishBlockResponse.StatusCode !=
                            StatusCode.BroadcastButFailedValidation)
                        {
                            throw new Exception(
                                $"Error response from publish: {(int) publishBlockResponse.StatusCode} {publishBlockResponse.StatusCode}.");
                        }

                        bool nodeAccepted = publishBlockResponse.StatusCode == StatusCode.Success;
                        // TODO: Log warning if not accepted? Not sure what else we could do.
                        _validatorState.ClearProposalDutyForSlot(slot);
                    }
                }
                catch (Exception ex)
                {
                    Log.ExceptionProcessingProposalDuty(_logger, slot, blsPublicKey, ex.Message, ex);
                }
                finally
                {
                    activity.Stop();
                }
            }
        }

        public async Task UpdateDutiesActivityAsync(Epoch epoch, CancellationToken cancellationToken)
        {
            Activity activity = new Activity("update-duties");
            activity.Start();
            using var activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                activity.TraceId, activity.SpanId);
            try
            {
                IList<BlsPublicKey> publicKeys = _validatorKeyProvider.GetPublicKeys();

                ApiResponse<IList<ValidatorDuty>> validatorDutiesResponse =
                    await _beaconNodeApi.ValidatorDutiesAsync(publicKeys, epoch, cancellationToken);
                if (validatorDutiesResponse.StatusCode != StatusCode.Success)
                {
                    Log.ErrorGettingValidatorDuties(_logger, (int) validatorDutiesResponse.StatusCode,
                        validatorDutiesResponse.StatusCode, null);
                    return;
                }

                // Record proposal duties first, in case there is an error
                foreach (ValidatorDuty validatorDuty in validatorDutiesResponse.Content)
                {
                    Slot? currentProposalSlot =
                        _validatorState.ProposalSlot.GetValueOrDefault(validatorDuty.ValidatorPublicKey);
                    if (validatorDuty.BlockProposalSlot.HasValue &&
                        validatorDuty.BlockProposalSlot != currentProposalSlot)
                    {
                        _validatorState.SetProposalDuty(validatorDuty.ValidatorPublicKey,
                            validatorDuty.BlockProposalSlot.Value);
                        if (_logger.IsInfo())
                            Log.ValidatorDutyProposalChanged(_logger, validatorDuty.ValidatorPublicKey, epoch,
                                validatorDuty.BlockProposalSlot.Value, null);
                    }
                }

                foreach (ValidatorDuty validatorDuty in validatorDutiesResponse.Content)
                {
                    Slot? currentAttestationSlot =
                        _validatorState.AttestationSlot.GetValueOrDefault(validatorDuty.ValidatorPublicKey);
                    Shard? currentAttestationShard =
                        _validatorState.AttestationShard.GetValueOrDefault(validatorDuty.ValidatorPublicKey);
                    if (validatorDuty.AttestationSlot.HasValue &&
                        (validatorDuty.AttestationSlot != currentAttestationSlot ||
                        validatorDuty.AttestationShard != currentAttestationShard))
                    {
                        _validatorState.SetAttestationDuty(validatorDuty.ValidatorPublicKey,
                            validatorDuty.AttestationSlot.Value,
                            validatorDuty.AttestationShard);
                        if (_logger.IsDebug())
                            LogDebug.ValidatorDutyAttestationChanged(_logger, validatorDuty.ValidatorPublicKey, epoch,
                                validatorDuty.AttestationSlot.Value, validatorDuty.AttestationShard, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ExceptionGettingValidatorDuties(_logger, ex.Message, ex);
            }
            finally
            {
                activity.Stop();
            }
        }

        public async Task UpdateForkVersionActivityAsync(CancellationToken cancellationToken)
        {
            // TODO: Should we be validating this?  i.e. check against config that it is the chain ID we are expecting?
            // TODO: At least for prior to cutover epoch (allows operation when fork epoch has not yet been reached and only one side is updated)
            // TODO: Once version is different for the current epoch, should disconnect.

            Activity activity = new Activity("update-fork-version");
            activity.Start();
            using var activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                activity.TraceId, activity.SpanId);
            try
            {
                var forkResponse = await _beaconNodeApi.GetNodeForkAsync(cancellationToken).ConfigureAwait(false);
                if (forkResponse.StatusCode == StatusCode.Success)
                {
                    await _beaconChainInformation.SetForkAsync(forkResponse.Content).ConfigureAwait(false);
                }
                else
                {
                    Log.ErrorGettingForkVersion(_logger, (int) forkResponse.StatusCode, forkResponse.StatusCode, null);
                }
            }
            catch (Exception ex)
            {
                Log.ExceptionGettingForkVersion(_logger, ex.Message, ex);
            }
            finally
            {
                activity.Stop();
            }
        }

        public async Task UpdateSyncStatusActivityAsync(CancellationToken cancellationToken)
        {
            Activity activity = new Activity("update-sync-status");
            activity.Start();
            using var activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                activity.TraceId, activity.SpanId);
            try
            {
                var syncingResponse = await _beaconNodeApi.GetSyncingAsync(cancellationToken).ConfigureAwait(false);
                if (syncingResponse.StatusCode == StatusCode.Success)
                {
                    await _beaconChainInformation.SetSyncStatus(syncingResponse.Content).ConfigureAwait(false);
                }
                else
                {
                    Log.ErrorGettingForkVersion(_logger, (int) syncingResponse.StatusCode, syncingResponse.StatusCode,
                        null);
                }
            }
            catch (Exception ex)
            {
                Log.ExceptionGettingSyncStatus(_logger, ex.Message, ex);
            }
            finally
            {
                activity.Stop();
            }
        }
    }
}