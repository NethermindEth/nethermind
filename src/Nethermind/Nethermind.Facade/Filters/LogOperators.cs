using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Blockchain.Filters.Topics
{

    public class LogOperators<Element>
    {

        public static IEnumerable<Element> Intersect(IEnumerator<Element>[] enumerators)
        {

            DictionarySortedSet<Element, IEnumerator<Element>> transactions = new();

            for (int i = 0; i < enumerators.Length; i++)
            {
                IEnumerator<Element> enumerator = enumerators[i];
                if (enumerator.MoveNext())
                {
                    transactions.Add(enumerator.Current!, enumerator);
                }
            }


            while (transactions.Count == enumerators.Length)
            {
                (Element blockNumber, IEnumerator<Element> enumerator) = transactions.Min;
                (Element blockNumber2, IEnumerator<Element> enumerator2) = transactions.Max;

                bool isIntersection = EqualityComparer<Element>.Default.Equals(blockNumber, blockNumber2);

                transactions.Remove(blockNumber);

                if (enumerator.MoveNext())
                {
                    transactions.Add(enumerator.Current!, enumerator);
                }

                if (isIntersection)
                {
                    yield return blockNumber;
                }
            }


        }

        public static IEnumerable<Element> Union(IEnumerator<Element>[] enumerators)
        {

            DictionarySortedSet<Element, IEnumerator<Element>> transactions = new();

            for (int i = 0; i < enumerators.Length; i++)
            {
                IEnumerator<Element> enumerator = enumerators[i];
                if (enumerator.MoveNext())
                {
                    transactions.Add(enumerator.Current!, enumerator);
                }
            }


            while (transactions.Count > 0)
            {
                (Element blockNumber, IEnumerator<Element> enumerator) = transactions.Min;

                transactions.Remove(blockNumber);

                bool isRepeated = false;

                if (transactions.Count > 0)
                {
                    (Element blockNumber2, IEnumerator<Element> enumerator2) = transactions.Min;
                    isRepeated = EqualityComparer<Element>.Default.Equals(blockNumber, blockNumber2);
                }


                if (enumerator.MoveNext())
                {

                    transactions.Add(enumerator.Current!, enumerator);
                }

                if (!isRepeated)
                {
                    yield return blockNumber;
                }

            }
        }

    }
}
