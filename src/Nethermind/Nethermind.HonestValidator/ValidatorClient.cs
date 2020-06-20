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
using Microsoft.Extensions.Caching.Memory;
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
        private readonly IMemoryCache _cache;
        private readonly ICryptographyService _cryptographyService;
        private readonly IOptionsMonitor<InitialValues> _initialValueOptions;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<SignatureDomains> _signatureDomainOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;
        private readonly IValidatorKeyProvider _validatorKeyProvider;
        private readonly ValidatorState _validatorState;

        public ValidatorClient(ILogger<ValidatorClient> logger,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<SignatureDomains> signatureDomainOptions,
            ICryptographyService cryptographyService,
            IBeaconNodeApi beaconNodeApi,
            IValidatorKeyProvider validatorKeyProvider,
            BeaconChainInformation beaconChainInformation)
        {
            _logger = logger;
            _initialValueOptions = initialValueOptions;
            _timeParameterOptions = timeParameterOptions;
            _signatureDomainOptions = signatureDomainOptions;
            _cryptographyService = cryptographyService;
            _beaconNodeApi = beaconNodeApi;
            _validatorKeyProvider = validatorKeyProvider;
            _beaconChainInformation = beaconChainInformation;

            _validatorState = new ValidatorState();
            _cache = new MemoryCache(new MemoryCacheOptions());
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

        public ulong GetAggregationTime(BeaconChainInformation beaconChainInformation, Slot slot)
        {
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            ulong startTimeOfSlot = beaconChainInformation.GenesisTime + timeParameters.SecondsPerSlot * slot;

            // Aggregate 2/3 way through slot
            ulong aggregationTime = startTimeOfSlot + (_timeParameterOptions.CurrentValue.SecondsPerSlot * 2 / 3);

            return aggregationTime;
        }

        public async Task<BlsSignature> GetAttestationSignatureAsync(Attestation unsignedAttestation,
            BlsPublicKey blsPublicKey)
        {
            Fork fork = _beaconChainInformation.Fork;
            Epoch epoch = ComputeEpochAtSlot(unsignedAttestation.Data.Slot);

            ForkVersion forkVersion;
            if (epoch < fork.Epoch)
            {
                forkVersion = fork.PreviousVersion;
            }
            else
            {
                forkVersion = fork.CurrentVersion;
            }

            DomainType domainType = _signatureDomainOptions.CurrentValue.BeaconAttester;

            (DomainType domainType, ForkVersion forkVersion) cacheKey = (domainType, forkVersion);
            Domain attesterDomain =
                await _cache.GetOrCreateAsync(cacheKey,
                    entry => { return Task.FromResult(ComputeDomain(domainType, forkVersion)); }).ConfigureAwait(false);

            Root attestationDataRoot = _cryptographyService.HashTreeRoot(unsignedAttestation.Data);
            Root signingRoot = ComputeSigningRoot(attestationDataRoot, attesterDomain);

            BlsSignature signature = _validatorKeyProvider.SignRoot(blsPublicKey, signingRoot);

            return signature;
        }

        public ulong GetAttestationTime(BeaconChainInformation beaconChainInformation, Slot slot)
        {
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            ulong startTimeOfSlot = beaconChainInformation.GenesisTime + timeParameters.SecondsPerSlot * slot;

            // Attest 1/3 way through slot
            ulong attestationTime = startTimeOfSlot + (_timeParameterOptions.CurrentValue.SecondsPerSlot / 3);

            return attestationTime;
        }

        public BlsSignature GetBlockSignature(BeaconBlock block, BlsPublicKey blsPublicKey)
        {
            Fork fork = _beaconChainInformation.Fork;
            Epoch epoch = ComputeEpochAtSlot(block.Slot);

            ForkVersion forkVersion;
            if (epoch < fork.Epoch)
            {
                forkVersion = fork.PreviousVersion;
            }
            else
            {
                forkVersion = fork.CurrentVersion;
            }

            DomainType domainType = _signatureDomainOptions.CurrentValue.BeaconProposer;
            Domain proposerDomain = ComputeDomain(domainType, forkVersion);

            /*
            JsonSerializerOptions options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            options.ConfigureNethermindCore2();
            string blockJson = System.Text.Json.JsonSerializer.Serialize(block, options);
            */

            Root blockRoot = _cryptographyService.HashTreeRoot(block);
            Root signingRoot = ComputeSigningRoot(blockRoot, proposerDomain);

            BlsSignature signature = _validatorKeyProvider.SignRoot(blsPublicKey, signingRoot);

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
            Fork fork = _beaconChainInformation.Fork;
            Epoch epoch = ComputeEpochAtSlot(slot);

            ForkVersion forkVersion;
            if (epoch < fork.Epoch)
            {
                forkVersion = fork.PreviousVersion;
            }
            else
            {
                forkVersion = fork.CurrentVersion;
            }

            DomainType domainType = _signatureDomainOptions.CurrentValue.Randao;
            Domain randaoDomain = ComputeDomain(domainType, forkVersion);

            Root epochRoot = _cryptographyService.HashTreeRoot(epoch);
            Root signingRoot = ComputeSigningRoot(epochRoot, randaoDomain);

            BlsSignature randaoReveal = _validatorKeyProvider.SignRoot(blsPublicKey, signingRoot);

            return randaoReveal;
        }

        public Slot GetNextAggregationSlotToCheck(BeaconChainInformation beaconChainInformation)
        {
            // Generally no point in aggregating for slots <= current (head) slot
            Slot slotToCheckAggregation =
                Slot.Max(beaconChainInformation.LastAggregationSlotChecked + Slot.One,
                    beaconChainInformation.SyncStatus.CurrentSlot);
            return slotToCheckAggregation;
        }

        public Slot GetNextAttestationSlotToCheck(BeaconChainInformation beaconChainInformation)
        {
            // Generally no point in attesting for slots <= current (head) slot
            Slot slotToCheckAttestation =
                Slot.Max(beaconChainInformation.LastAttestationSlotChecked + Slot.One,
                    beaconChainInformation.SyncStatus.CurrentSlot);
            return slotToCheckAttestation;
        }

        public async Task OnTickAsync(BeaconChainInformation beaconChainInformation, ulong time,
            CancellationToken cancellationToken)
        {
            // update time
            await beaconChainInformation.SetTimeAsync(time).ConfigureAwait(false);
            Slot currentSlot = GetCurrentSlot(beaconChainInformation);

            // Once at start of each slot, confirm responsibilities (could have been a reorg), then do proposal
            bool shouldCheckStartSlot = currentSlot > beaconChainInformation.LastStartSlotChecked;
            if (shouldCheckStartSlot)
            {
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
                    Slot.Max(beaconChainInformation.LastStartSlotChecked,
                        beaconChainInformation.SyncStatus.CurrentSlot) +
                    Slot.One;

                LogDebug.ProcessingSlotStart(_logger, slotToCheck, currentSlot,
                    beaconChainInformation.SyncStatus.CurrentSlot,
                    null);

                // Slot is set before processing; if there is an error (in process; update duties has a try/catch), it will skip to the next slot
                // (maybe the error will be resolved; trade off of whether error can be fixed by retrying, e.g. network error,
                // but potentially getting stuck in a slot, vs missing a slot)
                // TODO: Maybe add a retry policy/retry count when to advance last slot checked regardless
                await beaconChainInformation.SetLastStartSlotChecked(slotToCheck);

                // TODO: Attestations should run checks one epoch ahead, for topic subscriptions, although this is more a beacon node thing to do.

                // Note that UpdateDuties will continue even if there is an error/issue (i.e. assume no change and process what we have)
                Epoch epochToCheck = ComputeEpochAtSlot(slotToCheck);
                // Check duties each slot, in case there has been a reorg 
                await UpdateDutiesActivityAsync(epochToCheck, cancellationToken).ConfigureAwait(false);

                await ProcessProposalDutiesAsync(slotToCheck, cancellationToken).ConfigureAwait(false);

                // If upcoming attester, join (or change) topics
                // Subscribe to topics
            }

            // Attestation is done 1/3 way through slot
            Slot nextAttestationSlot = GetNextAttestationSlotToCheck(beaconChainInformation);
            ulong nextAttestationTime = GetAttestationTime(beaconChainInformation, nextAttestationSlot);
            if (beaconChainInformation.Time > nextAttestationTime)
            {
                // In theory, there could be a reorg between start of slot and attestation, changing
                // the attestation requirements, but currently (2020-05-24) only checking at start 
                // of slot (above).

                LogDebug.ProcessingSlotAttestations(_logger, nextAttestationSlot, beaconChainInformation.Time, null);
                await beaconChainInformation.SetLastAttestationSlotChecked(nextAttestationSlot);
                await ProcessAttestationDutiesAsync(nextAttestationSlot, cancellationToken).ConfigureAwait(false);
            }

            // TODO: Aggregation is done 2/3 way through slot
        }

        public async Task ProcessAttestationDutiesAsync(Slot slot, CancellationToken cancellationToken)
        {
            // If attester, get attestation, sign attestation, return to node

            IList<(BlsPublicKey, CommitteeIndex)>
                attestationDutyList = _validatorState.GetAttestationDutyForSlot(slot);

            foreach ((BlsPublicKey validatorPublicKey, CommitteeIndex index) in attestationDutyList)
            {
                Activity activity = new Activity("process-attestation-duty");
                activity.Start();
                using IDisposable activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                    activity.TraceId, activity.SpanId);
                try
                {
                    if (_logger.IsDebug())
                        LogDebug.RequestingAttestationFor(_logger, slot, _beaconChainInformation.Time,
                            validatorPublicKey,
                            null);

                    ApiResponse<Attestation> newAttestationResponse = await _beaconNodeApi
                        .NewAttestationAsync(validatorPublicKey, false, slot, index, cancellationToken)
                        .ConfigureAwait(false);
                    if (newAttestationResponse.StatusCode == StatusCode.Success)
                    {
                        Attestation unsignedAttestation = newAttestationResponse.Content;
                        BlsSignature attestationSignature =
                            await GetAttestationSignatureAsync(unsignedAttestation, validatorPublicKey)
                                .ConfigureAwait(false);
                        Attestation signedAttestation = new Attestation(unsignedAttestation.AggregationBits,
                            unsignedAttestation.Data, attestationSignature);

                        // TODO: Getting one attestation at a time probably isn't very scalable.
                        // All validators are attesting the same data, just in different committees with different indexes
                        // => Get the data once, group relevant validators by committee, sign and aggregate within each
                        // committee (marking relevant aggregation bits), then publish one pre-aggregated value? 

                        if (_logger.IsDebug())
                            LogDebug.PublishingSignedAttestation(_logger, slot, index,
                                validatorPublicKey.ToShortString(),
                                signedAttestation.Data,
                                signedAttestation.Signature.ToString().Substring(0, 10), null);

                        ApiResponse publishAttestationResponse = await _beaconNodeApi
                            .PublishAttestationAsync(signedAttestation, cancellationToken)
                            .ConfigureAwait(false);
                        if (publishAttestationResponse.StatusCode != StatusCode.Success &&
                            publishAttestationResponse.StatusCode !=
                            StatusCode.BroadcastButFailedValidation)
                        {
                            throw new Exception(
                                $"Error response from publish: {(int) publishAttestationResponse.StatusCode} {publishAttestationResponse.StatusCode}.");
                        }

                        bool nodeAccepted = publishAttestationResponse.StatusCode == StatusCode.Success;
                        // TODO: Log warning if not accepted? Not sure what else we could do.
                    }
                }
                catch (Exception ex)
                {
                    Log.ExceptionProcessingAttestationDuty(_logger, slot, validatorPublicKey, ex.Message, ex);
                }
                finally
                {
                    activity.Stop();
                }

                _validatorState.ClearAttestationDutyForSlot(slot);
            }
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
                using IDisposable activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
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
            using IDisposable activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
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
                    if (validatorDuty.BlockProposalSlot.HasValue)
                    {
                        Slot? currentProposalSlot =
                            _validatorState.ProposalSlot.GetValueOrDefault(validatorDuty.ValidatorPublicKey);
                        bool needsProposalUpdate = validatorDuty.BlockProposalSlot != currentProposalSlot;
                        if (needsProposalUpdate)
                        {
                            _validatorState.SetProposalDuty(validatorDuty.ValidatorPublicKey,
                                validatorDuty.BlockProposalSlot.Value);
                            if (_logger.IsInfo())
                                Log.ValidatorDutyProposalChanged(_logger, validatorDuty.ValidatorPublicKey, epoch,
                                    validatorDuty.BlockProposalSlot.Value, null);
                        }
                    }
                }

                foreach (ValidatorDuty validatorDuty in validatorDutiesResponse.Content)
                {
                    if (validatorDuty.AttestationSlot.HasValue)
                    {
                        bool needsAttestationUpdate;
                        if (_validatorState.AttestationSlotAndIndex.TryGetValue(validatorDuty.ValidatorPublicKey,
                            out (Slot, CommitteeIndex) currentValue))
                        {
                            (Slot currentAttestationSlot, CommitteeIndex currentAttestationIndex) = currentValue;
                            needsAttestationUpdate = validatorDuty.AttestationSlot != currentAttestationSlot ||
                                                     validatorDuty.AttestationIndex != currentAttestationIndex;
                        }
                        else
                        {
                            needsAttestationUpdate = true;
                        }

                        if (needsAttestationUpdate)
                        {
                            _validatorState.SetAttestationDuty(validatorDuty.ValidatorPublicKey,
                                validatorDuty.AttestationSlot.Value,
                                validatorDuty.AttestationIndex!.Value);
                            if (_logger.IsDebug())
                                LogDebug.ValidatorDutyAttestationChanged(_logger, validatorDuty.ValidatorPublicKey,
                                    epoch,
                                    validatorDuty.AttestationSlot.Value, validatorDuty.AttestationIndex.Value, null);
                        }
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
            using IDisposable activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                activity.TraceId, activity.SpanId);
            try
            {
                ApiResponse<Fork> forkResponse =
                    await _beaconNodeApi.GetNodeForkAsync(cancellationToken).ConfigureAwait(false);
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
            using IDisposable activityScope = _logger.BeginScope("[TraceId, {TraceId}], [SpanId, {SpanId}]",
                activity.TraceId, activity.SpanId);
            try
            {
                ApiResponse<Syncing> syncingResponse =
                    await _beaconNodeApi.GetSyncingAsync(cancellationToken).ConfigureAwait(false);
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