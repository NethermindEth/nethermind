#!/usr/bin/env python3
"""genesis_to_chainspec.py GENESIS.json NAME > chainspec.json

Writes the Nethermind chain spec of an Arkiv db-chain network from its execution genesis file (the one
db-chain-networks publishes, carrying config.arkiv.golemdb). The chain is post-merge from block 0 with every fork
up to Prague active at genesis; on top of Ethereum's rules it carries one consensus rule, Arkiv's base-fee floor —
next base fee = max(EIP-1559(parent), genesis base fee) — which is Nethermind's Eip1559BaseFeeMinValue.

The genesis header's stateRoot is GolemDB's root over these accounts, not the account trie's: the Arkiv plugin
substitutes it when it builds block 0, from the same genesis file (Arkiv.GenesisFile).
"""
import json
import sys

FORKS_BY_BLOCK = ["homesteadBlock", "eip150Block", "eip155Block", "eip158Block", "byzantiumBlock",
                  "constantinopleBlock", "petersburgBlock", "istanbulBlock", "berlinBlock", "londonBlock"]
SHANGHAI = ["eip3651", "eip3855", "eip3860", "eip4895"]
CANCUN = ["eip1153", "eip4788", "eip4844", "eip5656", "eip6780"]
PRAGUE = ["eip2537", "eip2935", "eip6110", "eip7002", "eip7251", "eip7623", "eip7702"]
INITIAL_BASE_FEE = 1_000_000_000  # reth's and Nethermind's London genesis default


def hex_(n):
    return hex(int(n, 0) if isinstance(n, str) else n)


def main():
    genesis = json.load(open(sys.argv[1]))
    name = sys.argv[2] if len(sys.argv) > 2 else "arkiv"
    config = genesis["config"]
    assert "arkiv" in config and "golemdb" in config["arkiv"], "not an Arkiv GolemDB genesis"
    for fork in FORKS_BY_BLOCK:
        assert config.get(fork, 0) == 0, f"{fork} is not at genesis"
    for fork in ("shanghaiTime", "cancunTime", "pragueTime"):
        assert config.get(fork) == 0, f"{fork} is not at genesis"
    assert config.get("terminalTotalDifficulty") == 0 and "osakaTime" not in config

    base_fee = int(genesis.get("baseFeePerGas", hex(INITIAL_BASE_FEE)), 0)
    params = {
        "chainID": hex(config["chainId"]),
        "networkID": hex(config["chainId"]),
        "gasLimitBoundDivisor": "0x400",
        "accountStartNonce": "0x0",
        "maximumExtraDataSize": "0x20",
        "minGasLimit": "0x1388",
        "maxCodeSize": "0x6000",
        "maxCodeSizeTransition": "0x0",
        "terminalTotalDifficulty": "0x0",
        "MergeForkIdTransition": "0x0",
        # Without one, the mainnet address: Arkiv runs no EVM, so no deposit is ever logged and requests stay empty.
        "depositContractAddress": config.get("depositContractAddress", "0x00000000219ab540356cBB839Cbe05303d7705Fa"),
        # Arkiv's base-fee floor: arkiv-reth-chainspec, next_block_base_fee = max(eip1559(parent), genesis base fee).
        "eip1559BaseFeeMinValue": hex(base_fee),
        "eip1559BaseFeeMinValueTransition": "0x0",
    }
    for eip in ["eip140", "eip145", "eip150", "eip152", "eip155", "eip158", "eip160", "eip161abc", "eip161d",
                "eip211", "eip214", "eip658", "eip1014", "eip1052", "eip1108", "eip1283", "eip1283Disable",
                "eip1344", "eip1559", "eip1884", "eip2028", "eip2200", "eip2565", "eip2929", "eip2930",
                "eip3198", "eip3529", "eip3541"]:
        params[f"{eip}Transition"] = "0x0"
    for eip in SHANGHAI + CANCUN + PRAGUE:
        params[f"{eip}TransitionTimestamp"] = "0x0"
    # The forks' own defaults (EIP-7691) when the file names none, as reth applies them.
    schedule = config.get("blobSchedule") or {"prague": {"target": 6, "max": 9, "baseFeeUpdateFraction": 5007716}}
    # Every fork is at timestamp 0, so Prague's blob schedule is the one in force.
    params["blobSchedule"] = [{"timestamp": "0x0", "target": schedule["prague"]["target"], "max": schedule["prague"]["max"],
                               "baseFeeUpdateFraction": hex(schedule["prague"]["baseFeeUpdateFraction"])}]

    accounts = {}
    for address, account in genesis["alloc"].items():
        entry = {"balance": hex_(account.get("balance", "0x0"))}
        if account.get("nonce"):
            entry["nonce"] = hex_(account["nonce"])
        if account.get("code"):
            entry["code"] = account["code"]
        if account.get("storage"):
            entry["storage"] = account["storage"]
        accounts[address if address.startswith("0x") else "0x" + address] = entry

    spec = {
        "name": name,
        "dataDir": name,
        "engine": {"Ethash": {}},
        "params": params,
        "genesis": {
            "seal": {"ethereum": {"nonce": genesis.get("nonce", "0x0"), "mixHash": genesis.get("mixhash", "0x" + "00" * 32)}},
            "difficulty": genesis.get("difficulty", "0x0"),
            "author": genesis.get("coinbase", "0x" + "00" * 20),
            "timestamp": hex_(genesis["timestamp"]),
            "parentHash": genesis.get("parentHash", "0x" + "00" * 32),
            "extraData": genesis.get("extraData") or "0x",
            "gasLimit": hex_(genesis["gasLimit"]),
            "baseFeePerGas": hex(base_fee),
        },
        "nodes": [],
        "accounts": accounts,
    }
    json.dump(spec, sys.stdout, indent=2)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
