using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
            var beaconChainUtility = new BeaconChainUtility(miscellaneousParameterOptions, gweiValueOptions, timeParameterOptions, cryptographyService);
            var beaconStateAccessor = new BeaconStateAccessor(miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, cryptographyService, beaconChainUtility);
            var beaconStateTransition = new BeaconStateTransition(loggerFactory.CreateLogger<BeaconStateTransition>(), miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions, beaconChainUtility, beaconStateAccessor);

            var numberOfValidators = (ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * 10;
            var state = TestGenesis.CreateGenesisState(chainConstants, miscellaneousParameterOptions.CurrentValue, initialValueOptions.CurrentValue, gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue, numberOfValidators);

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
                checkpoints[1],
                sufficientSupport,
                messedUpTarget: false);

            // process
            RunProcessJustificationAndFinalization(beaconStateTransition, timeParameterOptions.CurrentValue, state);

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

        [DataTestMethod]
        [DataRow((ulong)4, true)]
        [DataRow((ulong)4, false)]
        public void FinalizeOn23(ulong epochValue, bool sufficientSupport)
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
            var beaconChainUtility = new BeaconChainUtility(miscellaneousParameterOptions, gweiValueOptions, timeParameterOptions, cryptographyService);
            var beaconStateAccessor = new BeaconStateAccessor(miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, cryptographyService, beaconChainUtility);
            var beaconStateTransition = new BeaconStateTransition(loggerFactory.CreateLogger<BeaconStateTransition>(), miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions, beaconChainUtility, beaconStateAccessor);

            var numberOfValidators = (ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * 10;
            var state = TestGenesis.CreateGenesisState(chainConstants, miscellaneousParameterOptions.CurrentValue, initialValueOptions.CurrentValue, gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue, numberOfValidators);

            epoch.ShouldBeGreaterThan(new Epoch(3));

            // Skip ahead to just before epoch
            var slot = new Slot((ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * (ulong)epoch - 1);
            state.SetSlot(slot);

            //# 43210 -- epochs ago
            //# 210xx  -- justification bitfield indices (pre shift)
            //# 3210x -- justification bitfield indices (post shift)
            //# 01*0. -- justification bitfield contents, . = this epoch, * is being justified now
            //# checkpoints for the epochs ago:
            var checkpoints = TestData.GetCheckpoints(epoch).ToArray();
            PutCheckpointsInBlockRoots(beaconChainUtility, timeParameterOptions.CurrentValue, state, checkpoints[0..2]);

            var oldFinalized = state.FinalizedCheckpoint;
            state.SetPreviousJustifiedCheckpoint(checkpoints[2]);
            state.SetCurrentJustifiedCheckpoint(checkpoints[2]);
            // # mock 3rd latest epoch as justified (index is pre-shift)
            var justificationBits = new BitArray(chainConstants.JustificationBitsLength);
            justificationBits[1] = true;
            state.SetJustificationBits(justificationBits);

            // # mock the 2nd latest epoch as justifiable, with 3rd as source
            AddMockAttestations(beaconChainUtility,
                beaconStateAccessor,
                miscellaneousParameterOptions.CurrentValue,
                timeParameterOptions.CurrentValue,
                state,
                new Epoch((ulong)epoch - 2),
                checkpoints[2],
                checkpoints[1],
                sufficientSupport,
                messedUpTarget: false);

            // process
            RunProcessJustificationAndFinalization(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            state.PreviousJustifiedCheckpoint.ShouldBe(checkpoints[2]); // changed to old current
            if (sufficientSupport)
            {
                state.CurrentJustifiedCheckpoint.ShouldBe(checkpoints[1]); // changed to 2nd latest
                state.FinalizedCheckpoint.ShouldBe(checkpoints[2]); // finalized old previous justified epoch
            }
            else
            {
                state.CurrentJustifiedCheckpoint.ShouldBe(checkpoints[2]); // still old current
                state.FinalizedCheckpoint.ShouldBe(oldFinalized); // no new finalized
            }
        }

        [DataTestMethod]
        [DataRow((ulong)6, true)]
        [DataRow((ulong)6, false)]
        public void FinalizeOn123(ulong epochValue, bool sufficientSupport)
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
            var beaconChainUtility = new BeaconChainUtility(miscellaneousParameterOptions, gweiValueOptions, timeParameterOptions, cryptographyService);
            var beaconStateAccessor = new BeaconStateAccessor(miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, cryptographyService, beaconChainUtility);
            var beaconStateTransition = new BeaconStateTransition(loggerFactory.CreateLogger<BeaconStateTransition>(), miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions, beaconChainUtility, beaconStateAccessor);

            var numberOfValidators = (ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * 10;
            var state = TestGenesis.CreateGenesisState(chainConstants, miscellaneousParameterOptions.CurrentValue, initialValueOptions.CurrentValue, gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue, numberOfValidators);

            epoch.ShouldBeGreaterThan(new Epoch(5));

            // Skip ahead to just before epoch
            var slot = new Slot((ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * (ulong)epoch - 1);
            state.SetSlot(slot);

            //# 43210 -- epochs ago
            //# 210xx  -- justification bitfield indices (pre shift)
            //# 3210x -- justification bitfield indices (post shift)
            //# 011*. -- justification bitfield contents, . = this epoch, * is being justified now
            //# checkpoints for the epochs ago:
            var checkpoints = TestData.GetCheckpoints(epoch).ToArray();
            PutCheckpointsInBlockRoots(beaconChainUtility, timeParameterOptions.CurrentValue, state, checkpoints[0..4]);

            var oldFinalized = state.FinalizedCheckpoint;
            state.SetPreviousJustifiedCheckpoint(checkpoints[4]);
            state.SetCurrentJustifiedCheckpoint(checkpoints[2]);
            //# mock 3rd latest epochs as justified (index is pre-shift)
            var justificationBits = new BitArray(chainConstants.JustificationBitsLength);
            justificationBits[1] = true;
            state.SetJustificationBits(justificationBits);

            //# mock the 2nd latest epoch as justifiable, with 5th as source
            AddMockAttestations(beaconChainUtility,
                beaconStateAccessor,
                miscellaneousParameterOptions.CurrentValue,
                timeParameterOptions.CurrentValue,
                state,
                new Epoch((ulong)epoch - 2),
                checkpoints[4],
                checkpoints[1],
                sufficientSupport,
                messedUpTarget: false);

            //# mock the 1st latest epoch as justifiable, with 3rd as source
            AddMockAttestations(beaconChainUtility,
                beaconStateAccessor,
                miscellaneousParameterOptions.CurrentValue,
                timeParameterOptions.CurrentValue,
                state,
                new Epoch((ulong)epoch - 1),
                checkpoints[2],
                checkpoints[0],
                sufficientSupport,
                messedUpTarget: false);

            // process
            RunProcessJustificationAndFinalization(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            state.PreviousJustifiedCheckpoint.ShouldBe(checkpoints[2]); // changed to old current
            if (sufficientSupport)
            {
                state.CurrentJustifiedCheckpoint.ShouldBe(checkpoints[0]); //# changed to 1st latest
                state.FinalizedCheckpoint.ShouldBe(checkpoints[2]); // finalized old current
            }
            else
            {
                state.CurrentJustifiedCheckpoint.ShouldBe(checkpoints[2]); // still old current
                state.FinalizedCheckpoint.ShouldBe(oldFinalized); // no new finalized
            }
        }

        [DataTestMethod]
        [DataRow((ulong)3, true, false)]
        [DataRow((ulong)3, true, true)]
        [DataRow((ulong)3, false, false)]
        public void FinalizeOn12(ulong epochValue, bool sufficientSupport, bool messedUpTarget)
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
            var beaconChainUtility = new BeaconChainUtility(miscellaneousParameterOptions, gweiValueOptions, timeParameterOptions, cryptographyService);
            var beaconStateAccessor = new BeaconStateAccessor(miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, cryptographyService, beaconChainUtility);
            var beaconStateTransition = new BeaconStateTransition(loggerFactory.CreateLogger<BeaconStateTransition>(), miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions, beaconChainUtility, beaconStateAccessor);

            var numberOfValidators = (ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * 10;
            var state = TestGenesis.CreateGenesisState(chainConstants, miscellaneousParameterOptions.CurrentValue, initialValueOptions.CurrentValue, gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue, numberOfValidators);

            epoch.ShouldBeGreaterThan(new Epoch(2));

            // Skip ahead to just before epoch
            var slot = new Slot((ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * (ulong)epoch - 1);
            state.SetSlot(slot);

            //# 43210 -- epochs ago
            //# 210xx  -- justification bitfield indices (pre shift)
            //# 3210x -- justification bitfield indices (post shift)
            //# 001*. -- justification bitfield contents, . = this epoch, * is being justified now
            //# checkpoints for the epochs ago:
            var checkpoints = TestData.GetCheckpoints(epoch).ToArray();
            PutCheckpointsInBlockRoots(beaconChainUtility, timeParameterOptions.CurrentValue, state, checkpoints[0..1]);

            var oldFinalized = state.FinalizedCheckpoint;
            state.SetPreviousJustifiedCheckpoint(checkpoints[1]);
            state.SetCurrentJustifiedCheckpoint(checkpoints[1]);
            // # mock 2nd latest epoch as justified (this is pre-shift)
            var justificationBits = new BitArray(chainConstants.JustificationBitsLength);
            justificationBits[0] = true;
            state.SetJustificationBits(justificationBits);

            // # mock the 1st latest epoch as justifiable, with 2nd as source
            AddMockAttestations(beaconChainUtility,
                beaconStateAccessor,
                miscellaneousParameterOptions.CurrentValue,
                timeParameterOptions.CurrentValue,
                state,
                new Epoch((ulong)epoch - 1),
                checkpoints[1],
                checkpoints[0],
                sufficientSupport,
                messedUpTarget);

            // process
            RunProcessJustificationAndFinalization(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            state.PreviousJustifiedCheckpoint.ShouldBe(checkpoints[1]); // changed to old current
            if (sufficientSupport && !messedUpTarget)
            {
                state.CurrentJustifiedCheckpoint.ShouldBe(checkpoints[0]); // changed to 1st latest
                state.FinalizedCheckpoint.ShouldBe(checkpoints[1]); // finalized previous justified epoch
            }
            else
            {
                state.CurrentJustifiedCheckpoint.ShouldBe(checkpoints[1]); // still old current
                state.FinalizedCheckpoint.ShouldBe(oldFinalized); // no new finalized
            }
        }

        private void RunProcessJustificationAndFinalization(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(beaconStateTransition, timeParameters, state, "process_justification_and_finalization");
        }

        private void AddMockAttestations(BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor, MiscellaneousParameters miscellaneousParameters, TimeParameters timeParameters, BeaconState state, Epoch epoch, Checkpoint source, Checkpoint target, 
            bool sufficientSupport, bool messedUpTarget)
        {
            // we must be at the end of the epoch
            var isEndOfEpoch = ((ulong)state.Slot + 1) % (ulong)timeParameters.SlotsPerEpoch == 0;
            isEndOfEpoch.ShouldBeTrue();

            var previousEpoch = beaconStateAccessor.GetPreviousEpoch(state);
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);

            // state.SetXxx() methods called below instead
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
            if (currentEpoch != epoch && previousEpoch != epoch)
            {
                throw new Exception($"Cannot include attestations in epoch {epoch} from epoch {currentEpoch}");
            }

            var totalBalance = beaconStateAccessor.GetTotalActiveBalance(state);
            var remainingBalance = totalBalance * 2 / 3;

            var startSlot = beaconChainUtility.ComputeStartSlotOfEpoch(epoch);

            var oneSlot = new Slot(1);
            var oneCommitteeIndex = new CommitteeIndex(1);
            var beaconBlockRoot = new Hash32(Enumerable.Repeat((byte)0xff, 32).ToArray()); // irrelevant to testing
            for (var slot = startSlot; slot < startSlot + timeParameters.SlotsPerEpoch; slot += oneSlot)
            {
                var slotEpoch = beaconChainUtility.ComputeEpochAtSlot(slot);
                var committeesPerSlot = beaconStateAccessor.GetCommitteeCountAtSlot(state, slot);
                for(var index = new CommitteeIndex(); index < new CommitteeIndex(committeesPerSlot); index += oneCommitteeIndex)
                {
                    // Check if we already have had sufficient balance. (and undone if we don't want it).
                    // If so, do not create more attestations. (we do not have empty pending attestations normally anyway)
                    if (remainingBalance < Gwei.Zero)
                    {
                        return;
                    }

                    var committee = beaconStateAccessor.GetBeaconCommittee(state, slot, index);

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

                    Checkpoint attestationTarget;
                    if (messedUpTarget)
                    {
                        var messedUpRoot = new Hash32(Enumerable.Repeat((byte)0x99, 32).ToArray());
                        attestationTarget = new Checkpoint(target.Epoch, messedUpRoot);
                    }
                    else
                    {
                        attestationTarget = target;
                    }

                    var attestationData = new AttestationData(slot, index, beaconBlockRoot, source, attestationTarget);
                    var attestation = new PendingAttestation(aggregationBits, attestationData, new Slot(1), ValidatorIndex.None);

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

        //private Shard[] GetShardsForSlot(BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor, MiscellaneousParameters miscellaneousParameters, TimeParameters timeParameters, BeaconState state, Slot slot)
        //{
        //    var epoch = beaconChainUtility.ComputeEpochAtSlot(slot);
        //    Shard epochStartShard = beaconStateAccessor.GetStartShard(state, epoch);
        //    var committeeCount = beaconStateAccessor.GetCommitteeCount(state, epoch);
        //    var committeesPerSlot = committeeCount / (ulong)timeParameters.SlotsPerEpoch;
        //    var shard = (epochStartShard + new Shard(committeesPerSlot * (ulong)(slot % timeParameters.SlotsPerEpoch))) % miscellaneousParameters.ShardCount;
        //    var shards = Enumerable.Range(0, (int)committeesPerSlot).Select(x => shard + new Shard((ulong)x));
        //    return shards.ToArray();
        //}

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
