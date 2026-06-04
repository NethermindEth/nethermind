#!/usr/bin/env bash
set -euo pipefail

GENERATED_DIR="${GENERATED_DIR:-/Users/carmen/Documents/Work/xdc-subnet-test/generated}"
OUT="${OUT:-/Users/carmen/Documents/Work/nethermind/scripts/e2e/xdc-subnet-sync/config/chainspec-xdc-subnet-local.json}"

jq '{
  name: "xdc-subnet-local",
  engine: {
    XDPoSSubnet: {
      params: {
        period: .config.XDPoS.period,
        epoch: .config.XDPoS.epoch,
        reward: .config.XDPoS.reward,
        rewardCheckpoint: .config.XDPoS.rewardCheckpoint,
        gap: .config.XDPoS.gap,
        foundationWalletAddr: .config.XDPoS.foudationWalletAddr,
        switchEpoch: 0,
        switchBlock: .config.XDPoS.v2.switchBlock,
        v2Configs: [
          {
            MaxMasternodes: 108,
            SwitchRound: .config.XDPoS.v2.config.switchRound,
            CertificateThreshold: .config.XDPoS.v2.config.certificateThreshold,
            TimeoutSyncThreshold: .config.XDPoS.v2.config.timeoutSyncThreshold,
            TimeoutPeriod: .config.XDPoS.v2.config.timeoutPeriod,
            MinePeriod: .config.XDPoS.v2.config.minePeriod
          }
        ],
        MergeSignRange: 15,
        RangeReturnSigner: 150,
        DynamicGasLimitBlock: 99999999999999,
        tip2019Block: 1,
        BlackListHFNumber: 99999999999999,
        blackListedAddresses: [],
        masternodeVotingContract: "0x0000000000000000000000000000000000000088",
        blockSignerContract: "0x0000000000000000000000000000000000000089",
        randomizeSMCBinary: "0x0000000000000000000000000000000000000090",
        XDCXAddrBinary: "0x0000000000000000000000000000000000000091",
        tradingStateAddressBinary: "0x0000000000000000000000000000000000000092",
        XDCXLendingAddressBinary: "0x0000000000000000000000000000000000000093",
        XDCXLendingFinalizedTradeAddressBinary: "0x0000000000000000000000000000000000000094"
      }
    }
  },
  params: {
    chainId: .config.chainId,
    homesteadBlock: .config.homesteadBlock,
    eip150Block: .config.eip150Block,
    eip150Hash: .config.eip150Hash,
    eip155Block: .config.eip155Block,
    eip158Block: .config.eip158Block,
    byzantiumBlock: .config.byzantiumBlock
  },
  genesis: {
    nonce: .nonce,
    timestamp: .timestamp,
    extraData: .extraData,
    gasLimit: .gasLimit,
    difficulty: .difficulty,
    mixHash: .mixHash,
    coinbase: .coinbase,
    validators: [],
    nextValidators: [],
    penalties: [],
    number: .number,
    gasUsed: .gasUsed,
    parentHash: .parentHash
  },
  nodes: [],
  accounts: (
    .alloc
    | to_entries
    | map({ key: (.key | ltrimstr("0x")), value: .value })
    | from_entries
  )
}' "$GENERATED_DIR/genesis.json" > "$OUT"

echo "Wrote $OUT"
