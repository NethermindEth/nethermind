using System.Collections.Generic;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class ForkExtensions
    {
        public static SszContainer ToSszContainer(this Fork item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(Fork item)
        {
            yield return item.PreviousVersion.ToSszBasicVector();
            yield return item.CurrentVersion.ToSszBasicVector();
            // Epoch of latest fork
            yield return item.Epoch.ToSszBasicElement();
        }
    }
}
