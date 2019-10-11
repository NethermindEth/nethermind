using System;
using System.Collections.Generic;
using System.Linq;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class DepositDataExtensions
    {
        public static ReadOnlySpan<byte> HashTreeRoot(this IEnumerable<DepositData> list, int limit)
        {
            var tree = new SszTree(list.ToSszList(limit));
            return tree.HashTreeRoot();
        }

        public static ReadOnlySpan<byte> HashTreeRoot(this DepositData item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return tree.HashTreeRoot();
        }

        public static ReadOnlySpan<byte> SigningRoot(this DepositData item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, true)));
            return tree.HashTreeRoot();
        }

        public static SszContainer ToSszContainer(this DepositData item)
        {
            return new SszContainer(GetValues(item, false));
        }

        public static SszList ToSszList(this IEnumerable<DepositData> list, int limit)
        {
            return new SszList(list.Select(x => x.ToSszContainer()), limit);
        }

        private static IEnumerable<SszElement> GetValues(DepositData item, bool forSigning)
        {
            yield return new SszBasicVector(item.PublicKey);
            yield return new SszBasicVector(item.WithdrawalCredentials);
            yield return new SszBasicElement(item.Amount);
            if (!forSigning)
            {
                yield return new SszBasicVector(item.Signature);
            }
        }
    }
}
