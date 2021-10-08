/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;
using Nethermind.DataMarketplace.Providers.Repositories;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public class ReceiptProcessor : IReceiptProcessor
    {
        private readonly AbiSignature _dataDeliveryReceiptAbiSig = new AbiSignature("dataDeliveryReceipt",
            new AbiBytes(32),
            new AbiFixedLengthArray(new AbiUInt(32), 2));

        private readonly IProviderSessionRepository _sessionRepository;
        private readonly IAbiEncoder _abiEncoder;
        private readonly IEcdsa _ecdsa;
        private readonly ILogger _logger;

        public ReceiptProcessor(IProviderSessionRepository sessionRepository, IAbiEncoder abiEncoder, IEcdsa ecdsa,
            ILogManager logManager)
        {
            _sessionRepository = sessionRepository;
            _abiEncoder = abiEncoder;
            _ecdsa = ecdsa;
            _logger = logManager.GetClassLogger();
        }

        public async Task<bool> TryProcessAsync(ProviderSession session, Address consumer, INdmProviderPeer peer,
            DataDeliveryReceiptRequest receiptRequest, DataDeliveryReceipt deliveryReceipt)
        {
            var depositId = session.DepositId;
            var unitsRange = receiptRequest.UnitsRange;
            var abiHash = _abiEncoder.Encode(AbiEncodingStyle.Packed, _dataDeliveryReceiptAbiSig,
                receiptRequest.DepositId.Bytes, new[] {unitsRange.From, unitsRange.To});
            var address = _ecdsa.RecoverPublicKey(deliveryReceipt.Signature, Keccak.Compute(abiHash)).Address;
            if (!consumer.Equals(address))
            {
                if (_logger.IsWarn) _logger.Warn($"Recovered an invalid address: '{address}' (should be: '{consumer}') for delivery receipt for deposit: '{depositId}', consumer: '{session.ConsumerAddress}', session: '{session.Id}'.");
                session.SetDataAvailability(DataAvailability.DataDeliveryReceiptInvalid);
                await _sessionRepository.UpdateAsync(session);
                peer.SendDataAvailability(depositId, DataAvailability.DataDeliveryReceiptInvalid);

                return false;
            }

            var paidUnits = unitsRange.To - unitsRange.From + 1;
            if (_logger.IsInfo)_logger.Info($"Consumer: '{consumer}' has provided a valid receipt for deposit: '{receiptRequest.DepositId}', range: [{unitsRange.From}, {unitsRange.To}], paid units: {paidUnits}");
            if (receiptRequest.ReceiptsToMerge.Any())
            {
                if (_logger.IsInfo) _logger.Info($"Processing a merged receipt request for consumer: {session.ConsumerAddress}, session: '{session.Id} - units will not be updated.");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"Processing a receipt request for deposit: '{receiptRequest.DepositId}', consumer: {session.ConsumerAddress}, session: '{session.Id} - units will be updated.");
                var unpaidUnits = session.UnpaidUnits > paidUnits ? session.UnpaidUnits - paidUnits : 0;
                session.SetUnpaidUnits(unpaidUnits);
                session.AddPaidUnits(paidUnits);
                if (receiptRequest.IsSettlement)
                {
                    session.SetPaidUnits(paidUnits);
                    session.SettleUnits(paidUnits);
                    if (_logger.IsInfo) _logger.Info($"Settled {paidUnits} units for deposit: '{receiptRequest.DepositId}', consumer: {session.ConsumerAddress}, session: '{session.Id}'.");
                }
                
                await _sessionRepository.UpdateAsync(session);
            }

            var dataAvailability = session.DataAvailability;
            if (dataAvailability == DataAvailability.DataDeliveryReceiptInvalid ||
                 dataAvailability == DataAvailability.DataDeliveryReceiptNotProvided)
            {
                session.SetDataAvailability(DataAvailability.Available);
                await _sessionRepository.UpdateAsync(session);
                if (_logger.IsInfo) _logger.Info($"Updated previously set data availability: '{dataAvailability}' -> '{DataAvailability.Available}', consumer: {session.ConsumerAddress}, session: '{session.Id}'.");
            }

            return true;
        }
    }
}