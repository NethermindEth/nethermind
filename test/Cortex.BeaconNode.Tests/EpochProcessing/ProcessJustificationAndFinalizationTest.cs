using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Shouldly;

namespace Cortex.BeaconNode.Tests.EpochProcessing
{
    [TestClass]

    public class ProcessJustificationAndFinalizationTest
    {
        [DataTestMethod]
        [DataRow((ulong)5, true)]
        [DataRow((ulong)5, false)]
        public void FinalizeOn234(ulong epochValue, bool sufficientSupport)
        {
            // Arrange
            var loggerFactory = new LoggerFactory(new[] {
                new ConsoleLoggerProvider(TestOptionsMonitor.Create(new ConsoleLoggerOptions()))
            });
            var epoch = new Epoch(epochValue);
            TestConfiguration.GetMinimalConfiguration(
                out var chainConstants,
                out var miscellaneousParameterOptions,
                out var gweiValueOptions,
                out var initialValueOptions,
                out var timeParameterOptions,
                out var stateListLengthOptions,
                out var maxOperationsPerBlockOptions);
            var cryptographyService = new CryptographyService();
            var beaconChainUtility = new BeaconChainUtility(miscellaneousParameterOptions, timeParameterOptions, cryptographyService);
            var beaconStateAccessor = new BeaconStateAccessor(miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, cryptographyService, beaconChainUtility);
            var beaconStateTransition = new BeaconStateTransition(loggerFactory.CreateLogger<BeaconStateTransition>(), initialValueOptions, timeParameterOptions, stateListLengthOptions, beaconChainUtility, beaconStateAccessor);

            var numberOfValidators = (ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * 10;
            var state = TestData.CreateGenesisState(chainConstants, initialValueOptions.CurrentValue, gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue, numberOfValidators);

            epoch.ShouldBeGreaterThan(new Epoch(4));

            // Skip ahead to just before epoch
            var slot = new Slot((ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * (ulong)epoch - 1);
            state.SetSlot(slot);

            // 43210 -- epochs ago
            // 3210x -- justification bitfield indices
            // 11*0. -- justification bitfield contents, . = this epoch, * is being justified now
            // checkpoints for the epochs ago:
            var checkpoints = TestData.GetCheckpoints(epoch).ToArray();
            PutCheckpointsInBlockRoots(beaconChainUtility, timeParameterOptions.CurrentValue, state, checkpoints[0..3]);

            var oldFinalized = state.FinalizedCheckpoint;
            state.SetPreviousJustifiedCheckpoint(checkpoints[3]);
            state.SetCurrentJustifiedCheckpoint(checkpoints[2]);
            // mock 3rd and 4th latest epochs as justified (indices are pre-shift)
            var justificationBits = new BitArray(chainConstants.JustificationBitsLength);
            justificationBits[1] = true;
            justificationBits[2] = true;
            state.SetJustificationBits(justificationBits);

            // mock the 2nd latest epoch as justifiable, with 4th as source
            AddMockAttestations(beaconChainUtility, 
                beaconStateAccessor, 
                miscellaneousParameterOptions.CurrentValue, 
                timeParameterOptions.CurrentValue, 
                state, 
                new Epoch((ulong)epoch - 2), 
                checkpoints[3], 
                checkpoints[2], 
                sufficientSupport);

            // process
            RunProcessJustAndFin(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            state.PreviousJustifiedCheckpoint.ShouldBe(checkpoints[2]); // changed to old current
            if (sufficientSupport)
            {
                state.CurrentJustifiedCheckpoint.ShouldBe(checkpoints[1]); // changed to 2nd latest
                state.FinalizedCheckpoint.ShouldBe(checkpoints[3]); // finalized old previous justified epoch
            }
            else
            {
                state.CurrentJustifiedCheckpoint.ShouldBe(checkpoints[2]); // still old current
                state.FinalizedCheckpoint.ShouldBe(oldFinalized); // no new finalized
            }
        }

        private void RunProcessJustAndFin(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
        {
            RunEpochProcessingWith(beaconStateTransition, timeParameters, state, "process_justification_and_finalization");
        }

        /// <summary>
        /// Processes to the next epoch transition, up to and including the sub-transition named ``process_name``
        /// - pre-state('pre'), state before calling ``process_name``
        /// - post-state('post'), state after calling ``process_name``
        /// </summary>
        private void RunEpochProcessingWith(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state, string processName)
        {
            RunEpochProcessingTo(beaconStateTransition, timeParameters, state, processName);

            if (processName == "process_justification_and_finalization")
            {
                beaconStateTransition.ProcessJustificationAndFinalization(state);
                return;
            }

            throw new NotImplementedException();
        }

        /// <summary>
        /// Processes to the next epoch transition, up to, but not including, the sub-transition named ``process_name``
        /// </summary>
        private void RunEpochProcessingTo(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state, string processName)
        {
            var slot = state.Slot + (timeParameters.SlotsPerEpoch - state.Slot % timeParameters.SlotsPerEpoch);

            // transition state to slot before epoch state transition
            beaconStateTransition.ProcessSlots(state, slot - new Slot(1));

            // start transitioning, do one slot update before the epoch itself.
            beaconStateTransition.ProcessSlot(state);

            // process components of epoch transition before final-updates
            if (processName == "process_justification_and_finalization")
            {
                return;
            }
            // Note: only run when present. Later phases introduce more to the epoch-processing.
            beaconStateTransition.ProcessJustificationAndFinalization(state);

            if (processName == "process_crosslinks")
            {
                return;
            }

            throw new NotImplementedException();
        }

        private void AddMockAttestations(BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor, MiscellaneousParameters miscellaneousParameters, TimeParameters timeParameters, BeaconState state, Epoch epoch, Checkpoint source, Checkpoint target, bool sufficientSupport)
        {
            // we must be at the end of the epoch
            var isEndOfEpoch = ((ulong)state.Slot + 1) % (ulong)timeParameters.SlotsPerEpoch == 0;
            isEndOfEpoch.ShouldBeTrue();

            var previousEpoch = beaconStateAccessor.GetPreviousEpoch(state);
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);

            //IReadOnlyList<PendingAttestation> attestations;
            //if (currentEpoch == epoch)
            //{
            //    attestations = state.CurrentEpochAttestations;
            //}
            //else if (previousEpoch == epoch)
            //{
            //    attestations = state.PreviousEpochAttestations;
            //}
            //else
            if (epoch != currentEpoch && epoch != previousEpoch)
            {
                throw new Exception($"Cannot include attestations in epoch {epoch} from epoch {currentEpoch}");
            }

            var totalBalance = beaconStateAccessor.GetTotalActiveBalance(state);
            var remainingBalance = totalBalance * 2 / 3;

            var startSlot = beaconChainUtility.ComputeStartSlotOfEpoch(epoch);

            var addOne = new Slot(1);
            var beaconBlockRoot = new Hash32(Enumerable.Repeat((byte)0xff, 32).ToArray()); // irrelevant to testing
            for (var slot = startSlot; slot < startSlot + timeParameters.SlotsPerEpoch; slot += addOne)
            {
                var slotEpoch = beaconChainUtility.ComputeEpochOfSlot(slot);
                var shards = GetShardsForSlot(beaconChainUtility, beaconStateAccessor, miscellaneousParameters, timeParameters, state, slot);
                foreach (var shard in shards)
                {
                    // Check if we already have had sufficient balance. (and undone if we don't want it).
                    // If so, do not create more attestations. (we do not have empty pending attestations normally anyway)
                    if (remainingBalance < Gwei.Zero)
                    {
                        return;
                    }

                    var committee = beaconStateAccessor.GetCrosslinkCommittee(state, slotEpoch, shard);

                    // Create a bitfield filled with the given count per attestation,
                    // exactly on the right-most part of the committee field.
                    var aggregationBits = new BitArray(committee.Count);
                    for (var v = 0; v < (committee.Count * 2 / 3) + 1; v++)
                    {
                        if (remainingBalance <= Gwei.Zero)
                        {
                            break;
                        }
                        remainingBalance -= state.Validators[v].EffectiveBalance;
                        aggregationBits[v] = true;
                    }

                    // remove just one attester to make the marginal support insufficient
                    if (!sufficientSupport)
                    {
                        aggregationBits[1] = false;
                    }

                    var attestationData = new AttestationData(beaconBlockRoot, source, target, new Crosslink(shard));
                    var attestation = new PendingAttestation(aggregationBits, attestationData, new Slot(1));
                    if (currentEpoch == epoch)
                    {
                        state.AddCurrentAttestation(attestation);
                    }
                    else 
                    {
                        state.AddPreviousAttestation(attestation);
                    }
                }
            }
        }

        private Shard[] GetShardsForSlot(BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor, MiscellaneousParameters miscellaneousParameters, TimeParameters timeParameters, BeaconState state, Slot slot)
        {
            var epoch = beaconChainUtility.ComputeEpochOfSlot(slot);
            Shard epochStartShard = beaconStateAccessor.GetStartShard(state, epoch);
            var committeeCount = beaconStateAccessor.GetCommitteeCount(state, epoch);
            var committeesPerSlot = committeeCount / (ulong)timeParameters.SlotsPerEpoch;
            var shard = (epochStartShard + new Shard(committeesPerSlot * (ulong)(slot % timeParameters.SlotsPerEpoch))) % miscellaneousParameters.ShardCount;
            var shards = Enumerable.Range(0, (int)committeesPerSlot).Select(x => shard + new Shard((ulong)x));
            return shards.ToArray();
        }

        private void PutCheckpointsInBlockRoots(BeaconChainUtility beaconChainUtility, TimeParameters timeParameters, BeaconState state, Checkpoint[] checkpoints)
        {
            foreach (var checkpoint in checkpoints)
            {
                var startSlot = beaconChainUtility.ComputeStartSlotOfEpoch(checkpoint.Epoch);
                var slotIndex = startSlot % timeParameters.SlotsPerHistoricalRoot;
                state.SetBlockRoot(slotIndex, checkpoint.Root);
            }
        }
    }
}
