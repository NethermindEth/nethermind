//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Consumers.DataAssets;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;
using Nethermind.DataMarketplace.Consumers.Deposits.Repositories;
using Nethermind.DataMarketplace.Consumers.Providers;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class DepositManager : IDepositManager
    {
        private readonly AbiSignature _depositAbiSig = new AbiSignature("deposit",
            new AbiBytes(32),
            new AbiUInt(32),
            new AbiUInt(96),
            new AbiUInt(32),
            new AbiBytes(16),
            AbiType.Address,
            AbiType.Address);

        private readonly IDepositService _depositService;
        private readonly IDepositUnitsCalculator _depositUnitsCalculator;
        private readonly IDepositDetailsRepository _depositRepository;
        private readonly IDataAssetService _dataAssetService;
        private readonly IKycVerifier _kycVerifier;
        private readonly IProviderService _providerService;
        private readonly IAbiEncoder _abiEncoder;
        private readonly ICryptoRandom _cryptoRandom;
        private readonly ITimestamper _timestamper;
        private readonly uint _requiredBlockConfirmations;
        private readonly bool _disableSendingDepositTransaction;
        private readonly IWallet _wallet;
        private readonly IGasPriceService _gasPriceService;
        private readonly ILogger _logger;

        public DepositManager(IDepositService depositService, IDepositUnitsCalculator depositUnitsCalculator,
            IDataAssetService dataAssetService, IKycVerifier kycVerifier, IProviderService providerService,
            IAbiEncoder abiEncoder, ICryptoRandom cryptoRandom, IWallet wallet, IGasPriceService gasPriceService,
            IDepositDetailsRepository depositRepository, ITimestamper timestamper, ILogManager logManager,
            uint requiredBlockConfirmations, bool disableSendingDepositTransaction = false)
        {
            _depositService = depositService;
            _depositUnitsCalculator = depositUnitsCalculator;
            _depositRepository = depositRepository;
            _dataAssetService = dataAssetService;
            _kycVerifier = kycVerifier;
            _providerService = providerService;
            _abiEncoder = abiEncoder;
            _cryptoRandom = cryptoRandom;
            _timestamper = timestamper;
            _requiredBlockConfirmations = requiredBlockConfirmations;
            _disableSendingDepositTransaction = disableSendingDepositTransaction;
            _wallet = wallet;
            _gasPriceService = gasPriceService;
            _logger = logManager.GetClassLogger();
        }

        public async Task<DepositDetails?> GetAsync(Keccak depositId)
        {
            DepositDetails? deposit = await _depositRepository.GetAsync(depositId);
            if (deposit is null)
            {
                return null;
            }

            uint consumedUnits = await _depositUnitsCalculator.GetConsumedAsync(deposit);
            deposit.SetConsumedUnits(consumedUnits);

            return deposit;
        }

        public async Task<PagedResult<DepositDetails>> BrowseAsync(GetDeposits query)
        {
            PagedResult<DepositDetails> deposits = await _depositRepository.BrowseAsync(query);
            foreach (DepositDetails deposit in deposits.Items)
            {
                uint consumedUnits = await _depositUnitsCalculator.GetConsumedAsync(deposit);
                deposit.SetConsumedUnits(consumedUnits);
            }

            return deposits;
        }

        public async Task<Keccak?> MakeAsync(Keccak assetId, uint units, UInt256 value, Address address,
            UInt256? gasPrice = null)
        {
            if (!_wallet.IsUnlocked(address))
            {
                if (_logger.IsWarn) _logger.Warn($"Account: '{address}' is locked, can't make a deposit.");

                return null;
            }

            DataAsset? dataAsset = _dataAssetService.GetDiscovered(assetId);
            if (dataAsset is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Available data asset: '{assetId}' was not found.");

                return null;
            }

            if (!_dataAssetService.IsAvailable(dataAsset))
            {
                if (_logger.IsWarn) _logger.Warn($"Data asset: '{assetId}' is unavailable, state: {dataAsset.State}.");

                return null;
            }

            if (dataAsset.KycRequired && !await _kycVerifier.IsVerifiedAsync(assetId, address))
            {
                return null;
            }

            Address providerAddress = dataAsset.Provider.Address;
            INdmPeer? provider = _providerService.GetPeer(providerAddress);
            if (provider is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Provider nodes were not found for address: '{providerAddress}'.");

                return null;
            }

            if (dataAsset.MinUnits > units || dataAsset.MaxUnits < units)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid data request units: '{units}', min: '{dataAsset.MinUnits}', max: '{dataAsset.MaxUnits}'.");

                return null;
            }

            UInt256 unitsValue = units * dataAsset.UnitPrice;
            if (units * dataAsset.UnitPrice != value)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid data request value: '{value}', while it should be: '{unitsValue}'.");

                return null;
            }

            uint now = (uint) _timestamper.UnixTime.Seconds;
            uint expiryTime = now + (uint) dataAsset.Rules.Expiry.Value;
            expiryTime += dataAsset.UnitType == DataAssetUnitType.Unit ? 0 : units;
            byte[] pepper = _cryptoRandom.GenerateRandomBytes(16);
            byte[] abiHash = _abiEncoder.Encode(AbiEncodingStyle.Packed, _depositAbiSig, assetId.Bytes,
                units, value, expiryTime, pepper, dataAsset.Provider.Address, address);
            Keccak depositId = Keccak.Compute(abiHash);
            Deposit deposit = new Deposit(depositId, units, expiryTime, value);
            DepositDetails depositDetails = new DepositDetails(deposit, dataAsset, address, pepper, now,
                Enumerable.Empty<TransactionInfo>(), 0, requiredConfirmations: _requiredBlockConfirmations);
            UInt256 gasPriceValue = gasPrice is null || gasPrice.Value == 0
                ? await _gasPriceService.GetCurrentGasPriceAsync()
                : gasPrice.Value;
            await _depositRepository.AddAsync(depositDetails);

            Keccak? transactionHash = null;
            if (_logger.IsInfo) _logger.Info($"Created a deposit with id: '{depositId}', for data asset: '{assetId}', address: '{address}'.");
            if (_disableSendingDepositTransaction)
            {
                if (_logger.IsWarn) _logger.Warn("*** NDM sending deposit transaction is disabled ***");
            }
            else
            {
                transactionHash = await _depositService.MakeDepositAsync(address, deposit, gasPriceValue);
            }

            if (transactionHash == null)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to make a deposit with for data asset: '{assetId}', address: '{address}', gas price: {gasPriceValue} wei.");
                return null;
            }

            if (_logger.IsInfo) _logger.Info($"Sent a deposit with id: '{depositId}', transaction hash: '{transactionHash}' for data asset: '{assetId}', address: '{address}', gas price: {gasPriceValue} wei.");

            depositDetails.AddTransaction(TransactionInfo.Default(transactionHash, deposit.Value, gasPriceValue,
                _depositService.GasLimit, now));
            await _depositRepository.UpdateAsync(depositDetails);

            return depositId;
        }
    }
}
