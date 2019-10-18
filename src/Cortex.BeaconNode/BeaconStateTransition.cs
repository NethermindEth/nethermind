using System;
using System.Collections.Generic;
using System.Text;
using Cortex.Containers;
using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode
{
    public class BeaconStateTransition
    {
        private readonly ILogger _logger;

        public BeaconStateTransition(ILogger<BeaconStateTransition> logger)
        {
            _logger = logger;
        }

        public void ProcessSlots(BeaconState state, Slot slot)
        {
            _logger.LogDebug(Event.ProcessSlots, "Process slots to {Slot} for state {BeaconState}", slot, state);
        }

        public void ProcessSlot(BeaconState state)
        {
            _logger.LogDebug(Event.ProcessSlot, "Process current slot for state {BeaconState}", state);
        }

        public void ProcessJustificationAndFinalization(BeaconState state)
        {
            _logger.LogDebug(Event.ProcessJustificationAndFinalization, "Process justification and finalization state {BeaconState}", state);
        }
    }
}
