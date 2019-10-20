using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode
{
    public static class Event
    {
        // 1bxx preliminary
        public static readonly EventId TryGenesis = new EventId(1100, nameof(TryGenesis));
        public static readonly EventId InitializeBeaconState = new EventId(1101, nameof(InitializeBeaconState));

        // 2bxx completion
        public static readonly EventId ProcessDeposit = new EventId(2000, nameof(ProcessDeposit));
        public static readonly EventId ProcessSlots = new EventId(2001, nameof(ProcessSlots));
        public static readonly EventId ProcessSlot = new EventId(2002, nameof(ProcessSlot));
        public static readonly EventId ProcessJustificationAndFinalization = new EventId(2003, nameof(ProcessJustificationAndFinalization));
        public static readonly EventId ProcessEpoch = new EventId(2004, nameof(ProcessEpoch));

        // 4bxx warning

        // 5bxx error

        // 8bxx finalization

        // 9bxx critical
    }
}
