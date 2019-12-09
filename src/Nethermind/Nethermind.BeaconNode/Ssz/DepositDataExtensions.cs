using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class DepositDataExtensions
    {
        public static Hash32 HashTreeRoot(this IEnumerable<DepositData> list, ulong limit)
        {
            var tree = new SszTree(list.ToSszList(limit));
            return new Hash32(tree.HashTreeRoot());
        }

        public static Hash32 HashTreeRoot(this DepositData item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Hash32(tree.HashTreeRoot());
        }

        public static Hash32 SigningRoot(this DepositData item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, true)));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this DepositData item)
        {
            return new SszContainer(GetValues(item, false));
        }

        public static SszList ToSszList(this IEnumerable<DepositData> list, ulong limit)
        {
            return new SszList(list.Select(x => x.ToSszContainer()), limit);
        }

        private static IEnumerable<SszElement> GetValues(DepositData item, bool forSigning)
        {
            yield return new SszBasicVector(item.PublicKey.AsSpan());
            yield return new SszBasicVector(item.WithdrawalCredentials.AsSpan());
            yield return new SszBasicElement((ulong)item.Amount);
            if (!forSigning)
            {
                yield return item.Signature.ToSszBasicVector();
            }
        }
    }
}
