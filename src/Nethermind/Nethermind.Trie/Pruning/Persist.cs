// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public static class Persist
    {
        public static IPersistenceStrategy EveryBlock = Archive.Instance;

        public static IPersistenceStrategy EveryNBlock(long length)
            => new ConstantInterval(length);

        public static IPersistenceStrategy Or(this IPersistenceStrategy strategy, IPersistenceStrategy otherStrategy)
        {
            if (strategy is CompositePersistenceStrategy compositeStrategy)
            {
                return compositeStrategy.AddStrategy(otherStrategy);
            }
            else if (otherStrategy is CompositePersistenceStrategy otherCompositeStrategy)
            {
                return otherCompositeStrategy.AddStrategy(strategy);
            }
            else
            {
                return new CompositePersistenceStrategy(strategy, otherStrategy);
            }
        }
    }
}
