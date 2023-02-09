// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Snap.Storage
{
    public class SnapStorage
    {
        private UInt256 _bottomBlockNumber;
        private SnapLayer _bottomLayer;
        private Dictionary<UInt256, SnapLayer> _layers = new();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="leafNode">null if DELETE</param>
        public void AddLeafNode(Keccak path, TrieNode? leafNode, UInt256 blockNumber)
        {
            if (blockNumber < _bottomBlockNumber)
            {
                throw new ArgumentException($"Block number smaller than Bottom Block Number ({_bottomBlockNumber})", nameof(blockNumber));
            }

            ReorgLayers(blockNumber);

            SnapLayer layer;
            if (blockNumber == _bottomBlockNumber)
            {
                layer = _bottomLayer;
            }
            else
            {
                if (_layers.TryGetValue(blockNumber, out SnapLayer existingLayer))
                {
                    layer = existingLayer;
                }
                else
                {
                    layer = new();
                    _layers.Add(blockNumber, layer);
                }
            }

            layer[path] = leafNode;
        }

        public (Keccak path, TrieNode node)[] GetRange(Keccak startingHash, Keccak endHash, UInt256 blockNumber)
        {
            SnapLayer resultLayer = new();

            FlattenLayers(startingHash, endHash, _bottomLayer, resultLayer);

            for (UInt256 i = _bottomBlockNumber + 1; i <= blockNumber; i++)
            {
                if (_layers.TryGetValue(i, out SnapLayer layer))
                {
                    FlattenLayers(startingHash, endHash, layer, resultLayer);
                }
            }

            return resultLayer.Select(i => (i.Key, i.Value)).ToArray();
        }

        private void FlattenLayers(Keccak startingHash, Keccak endHash, SnapLayer sourceLayer, SnapLayer resultLayer)
        {
            foreach (var item in sourceLayer.Where(n => n.Key >= startingHash && n.Key <= endHash))
            {
                if (item.Value is null)
                {
                    resultLayer.Remove(item.Key);
                }
                else
                {
                    resultLayer[item.Key] = item.Value;
                }
            }
        }

        private void ReorgLayers(UInt256 maxBlockNumber)
        {
            if (_bottomLayer is null)
            {
                _bottomLayer = new();
                _bottomBlockNumber = maxBlockNumber;
                return;
            }

            UInt256 newBottomBlockNumber = maxBlockNumber - Constants.MaxDistanceFromHead;

            if (newBottomBlockNumber <= _bottomBlockNumber)
            {
                return;
            }

            for (UInt256 i = _bottomBlockNumber + 1; i <= newBottomBlockNumber; i++)
            {
                if (_layers.TryGetValue(i, out SnapLayer layer))
                {
                    foreach (var item in layer)
                    {
                        _bottomLayer[item.Key] = item.Value;
                    }

                    _layers.Remove(i);
                }
            }

            _bottomBlockNumber = newBottomBlockNumber;
        }
    }
}
