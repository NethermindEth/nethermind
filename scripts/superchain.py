# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import argparse
import json
import logging
import os
import os.path as path
import sha3
import tempfile
import tomllib
import zstandard as zstd
from functools import reduce
from io import BytesIO
from urllib.request import urlopen
from zipfile import ZipFile

SUPERCHAIN_REPOSITORY = "https://github.com/ethereum-optimism/superchain-registry/archive/refs/heads/main.zip"
IGNORED_CHAINS = ["arena-z-testnet", "creator-chain-testnet"]
IGNORED_L1S = ["sepolia-dev-0"]


def keccak(hex):
    return "0x" + sha3.keccak_256(bytes.fromhex(hex[2:])).hexdigest()


def lookup(dictionary, path):
    try:
        return reduce(lambda d, key: d[key], path, dictionary)
    except KeyError:
        return None


def merge_all(*dicts):
    def merge(left: dict, right: dict, path=[]):
        for key in right:
            if key in left:
                if isinstance(left[key], dict) and isinstance(right[key], dict):
                    merge(left[key], right[key], path + [str(key)])
                elif left[key] != right[key]:
                    # Prefer right over left
                    left[key] = right[key]
            else:
                left[key] = right[key]
        return left

    return reduce(merge, [{}, *dicts])


def filter_none(d):
    return {k: v for k, v in d.items() if v is not None}


def one_of(*args):
    for arg in args:
        if arg is not None:
            return arg
    return None


def fmap(f, optional):
    if optional is None:
        return None
    return f(optional)


def to_nethermind_accounts(genesis):
    alloc = lookup(genesis, ["alloc"])

    result = {}
    for address, account in alloc.items():
        result[f"0x{address}"] = account

    return result


def to_nethermind_chainspec(chain_name, l1, superchain, chain, genesis):
    constants = {
        "L1BeaconGenesisSlotTime": {
            "mainnet": 1606824023,
            "sepolia": 1655733600,
        },
        "L1ChainId": {
            "mainnet": 1,
            "sepolia": 11155111,
        },
        "DepositContractAddress": {
            "mainnet": "0x00000000219ab540356cbb839cbe05303d7705fa",
            "sepolia": "0x7f02c3e3c98b133055b8b348b2ac625669ed295d",
        },
    }

    # Preprocessing
    config = merge_all(superchain, chain)

    # We need to ensure that the Hardfork activation inheritance behavior is satisfied
    # https://github.com/ethereum-optimism/superchain-registry/blob/main/docs/hardfork-activation-inheritance.md
    # To derive, "It must not set a non-nil value for this activation time in its individual configuration file"
    config["hardforks"] = {k: v for k, v in config["hardforks"].items() if k in chain["hardforks"]}

    nethermind = {
        "name": lookup(config, ["name"]),
        "dataDir": f"{chain_name}-{l1}",
        "engine": {
            "Optimism": {
                "params": filter_none({
                    "regolithTimestamp": "0x0",
                    "bedrockBlockNumber": hex(lookup(config, ["genesis", "l2", "number"])),
                    "canyonTimestamp": fmap(hex, (lookup(config, ["hardforks", "canyon_time"]))),
                    "deltaTimestamp": fmap(hex, (lookup(config, ["hardforks", "delta_time"]))),
                    "ecotoneTimestamp": fmap(hex, (lookup(config, ["hardforks", "ecotone_time"]))),
                    "fjordTimestamp": fmap(hex, (lookup(config, ["hardforks", "fjord_time"]))),
                    "graniteTimestamp": fmap(hex, (lookup(config, ["hardforks", "granite_time"]))),
                    "holoceneTimestamp": fmap(hex, (lookup(config, ["hardforks", "holocene_time"]))),
                    "isthmusTimestamp": fmap(hex, (lookup(config, ["hardforks", "isthmus_time"]))),
                    "canyonBaseFeeChangeDenominator": str(lookup(config, ["optimism", "eip1559_denominator_canyon"])),
                    "l1FeeRecipient": "0x420000000000000000000000000000000000001A",
                    "l1BlockAddress": "0x4200000000000000000000000000000000000015",
                    "create2DeployerAddress": "0x13b0D85CcB8bf860b6b79AF3029fCA081AE9beF2",
                    "create2DeployerCode": "6080604052600436106100435760003560e01c8063076c37b21461004f578063481286e61461007157806356299481146100ba57806366cfa057146100da57600080fd5b3661004a57005b600080fd5b34801561005b57600080fd5b5061006f61006a366004610327565b6100fa565b005b34801561007d57600080fd5b5061009161008c366004610327565b61014a565b60405173ffffffffffffffffffffffffffffffffffffffff909116815260200160405180910390f35b3480156100c657600080fd5b506100916100d5366004610349565b61015d565b3480156100e657600080fd5b5061006f6100f53660046103ca565b610172565b61014582826040518060200161010f9061031a565b7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe082820381018352601f90910116604052610183565b505050565b600061015683836102e7565b9392505050565b600061016a8484846102f0565b949350505050565b61017d838383610183565b50505050565b6000834710156101f4576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152601d60248201527f437265617465323a20696e73756666696369656e742062616c616e636500000060448201526064015b60405180910390fd5b815160000361025f576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820181905260248201527f437265617465323a2062797465636f6465206c656e677468206973207a65726f60448201526064016101eb565b8282516020840186f5905073ffffffffffffffffffffffffffffffffffffffff8116610156576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152601960248201527f437265617465323a204661696c6564206f6e206465706c6f790000000000000060448201526064016101eb565b60006101568383305b6000604051836040820152846020820152828152600b8101905060ff815360559020949350505050565b61014e806104ad83390190565b6000806040838503121561033a57600080fd5b50508035926020909101359150565b60008060006060848603121561035e57600080fd5b8335925060208401359150604084013573ffffffffffffffffffffffffffffffffffffffff8116811461039057600080fd5b809150509250925092565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052604160045260246000fd5b6000806000606084860312156103df57600080fd5b8335925060208401359150604084013567ffffffffffffffff8082111561040557600080fd5b818601915086601f83011261041957600080fd5b81358181111561042b5761042b61039b565b604051601f82017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe0908116603f011681019083821181831017156104715761047161039b565b8160405282815289602084870101111561048a57600080fd5b826020860160208301376000602084830101528095505050505050925092509256fe608060405234801561001057600080fd5b5061012e806100206000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c8063249cb3fa14602d575b600080fd5b603c603836600460b1565b604e565b60405190815260200160405180910390f35b60008281526020818152604080832073ffffffffffffffffffffffffffffffffffffffff8516845290915281205460ff16608857600060aa565b7fa2ef4600d742022d532d4747cb3547474667d6f13804902513b2ec01c848f4b45b9392505050565b6000806040838503121560c357600080fd5b82359150602083013573ffffffffffffffffffffffffffffffffffffffff8116811460ed57600080fd5b80915050925092905056fea26469706673582212205ffd4e6cede7d06a5daf93d48d0541fc68189eeb16608c1999a82063b666eb1164736f6c63430008130033a2646970667358221220fdc4a0fe96e3b21c108ca155438d37c9143fb01278a3c1d274948bad89c564ba64736f6c63430008130033",
                })
            },
            "OptimismCL": merge_all(
                {
                    "GenesisSystemConfig": {
                        "BatcherAddr": lookup(config, ["genesis", "system_config", "batcherAddress"]),
                        "Overhead": lookup(config, ["genesis", "system_config", "overhead"]),
                        "Scalar": lookup(config, ["genesis", "system_config", "scalar"]),
                        "GasLimit": lookup(config, ["genesis", "system_config", "gasLimit"]),
                        "EIP1559Params": "0x0000000000000000",
                        "OperatorFeeParams": "0x0000000000000000000000000000000000000000000000000000000000000000",
                    },
                    "L1BeaconGenesisSlotTime": lookup(constants, ["L1BeaconGenesisSlotTime", l1]),
                    "L1ChainId": lookup(constants, ["L1ChainId", l1]),
                    "L1GenesisNumber": lookup(config, ["genesis", "l1", "number"]),
                    "L1GenesisHash": lookup(config, ["genesis", "l1", "hash"]),
                    "BatcherInboxAddress": lookup(config, ["batch_inbox_addr"]),
                    "L2BlockTime": lookup(config, ["block_time"]),
                    "SeqWindowSize": lookup(config, ["seq_window_size"]),
                    "ChannelTimeoutBedrock": 300,
                    "MaxSequencerDrift": lookup(config, ["max_sequencer_drift"]),
                    "SystemTransactionSender": "0xDeaDDEaDDeAdDeAdDEAdDEaddeAddEAdDEAd0001",
                    "SystemTransactionTo": "0x4200000000000000000000000000000000000015",
                    "Nodes": [
                        "/ip4/148.113.215.57/tcp/9223/p2p/16Uiu2HAm8aSUf4Stw7pKJEzsBiXWfV3gyWDxJymZ1XQN3nt1g8gu",
                        "/ip4/40.160.1.105/tcp/9223/p2p/16Uiu2HAky2Uha3XgRVaBaCHCGoMU972MgkanESat6ApyKU982UJZ",
                        "/ip4/144.76.17.55/tcp/30321/p2p/16Uiu2HAm6yHDvd1uqy9WPXwh1o57CtmyPLo8sugE2Ky8uzpksQFa",
                        "/ip4/16.62.148.134/tcp/9222/p2p/16Uiu2HAmFayrxPwx4p7U3ahZtE57KzecnyuqbUrFemzp7j66HV4Y",
                        "/ip4/167.235.91.92/tcp/9222/p2p/16Uiu2HAm4ro1o8Zhw6XfjxWhkbRwHF2TNNoK8ZM1cMUp3z1bU2Qv",
                        "/ip4/16.62.139.228/tcp/9222/p2p/16Uiu2HAmDVP7huC3uZy2Eifd8d5UN4Jiu2rHBUAgyF9CB956ftPs",
                        "/ip4/15.204.105.1/tcp/9222/p2p/16Uiu2HAm77PPudgtf94YN5DYi3PYRJETffKnT6dHG1Lwgb6tXiNg",
                        "/ip4/185.185.51.127/tcp/9003/p2p/16Uiu2HAmMF8ErQ9dp7cmf2e3du5wjib9RYVVnR6JmGGff8YPVS4t",
                        "/ip4/16.63.201.250/tcp/9222/p2p/16Uiu2HAkvVQqLJXzpTXwpaqF76bBEc8jJeRJAU1Z1eN9YKj154eJ",
                        "/ip4/135.181.216.232/tcp/900/p2p/16Uiu2HAmJdoZp5vFhLpYkvyYKotWNZW4rvunSc3jCtuHKkzCcssG3",
                        "/ip4/40.160.21.9/tcp/9222/p2p/16Uiu2HAmEbdyXFy3f8kbC86jqnn1EZ2WAdytjtioRJ7d8fnYc2Jw",
                        "/ip4/162.55.232.96/tcp/15629/p2p/16Uiu2HAmUwwtLFeHeJZL5T5oEAwgArjo3UbzkTgyku5twRnRNiqY",
                        "/ip4/176.9.123.83/tcp/9222/p2p/16Uiu2HAmU121HqRzryi8Dev3E4VoMMtG2Av6okTnbdQSK54EZMqU",
                        "/ip4/5.9.80.215/tcp/9222/p2p/16Uiu2HAkvrrKbiUkS6XzoXiBzcpLqkFSkpgpBPtxzRwRjEcgTdCB",
                    ],
                },
                lookup(config, ["roles"]),
                lookup(config, ["addresses"]),
            ),
        },
        "params": filter_none({
            "chainId": hex(lookup(config, ["chain_id"])),
            "gasLimitBoundDivisor": "0x400",
            "accountStartNonce": "0x0",
            "maximumExtraDataSize": "0x20",
            "minGasLimit": "0x1388",
            "forkBlock": "0x0",
            "maxCodeSize": "0x6000",
            "maxCodeSizeTransition": "0x0",
            "eip150Transition": "0x0",
            "eip160Transition": "0x0",
            "eip161abcTransition": "0x0",
            "eip161dTransition": "0x0",
            "eip155Transition": "0x0",
            "eip140Transition": "0x0",
            "eip211Transition": "0x0",
            "eip214Transition": "0x0",
            "eip658Transition": "0x0",
            "eip145Transition": "0x0",
            "eip1014Transition": "0x0",
            "eip1052Transition": "0x0",
            "eip1283Transition": "0x0",
            "eip1283DisableTransition": "0x0",
            "eip152Transition": "0x0",
            "eip1108Transition": "0x0",
            "eip1344Transition": "0x0",
            "eip1884Transition": "0x0",
            "eip2028Transition": "0x0",
            "eip2200Transition": "0x0",
            "eip2565Transition": "0x0",
            "eip2929Transition": "0x0",
            "eip2930Transition": "0x0",
            "eip1559Transition": hex(lookup(config, ["genesis", "l2", "number"])),
            "eip1559FeeCollectorTransition": hex(lookup(config, ["genesis", "l2", "number"])),
            "feeCollector": "0x4200000000000000000000000000000000000019",
            "eip1559ElasticityMultiplier": hex(lookup(config, ["optimism", "eip1559_elasticity"])),
            "eip1559BaseFeeMaxChangeDenominator": hex(lookup(config, ["optimism", "eip1559_denominator"])),
            "eip3198Transition": hex(lookup(config, ["genesis", "l2", "number"])),
            "eip3529Transition": hex(lookup(config, ["genesis", "l2", "number"])),
            "eip3541Transition": hex(lookup(config, ["genesis", "l2", "number"])),
            "eip4895TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "canyon_time"]))),
            "eip3651TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "canyon_time"]))),
            "eip3855TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "canyon_time"]))),
            "eip3860TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "canyon_time"]))),
            "eip1153TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "ecotone_time"]))),
            "eip4788TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "ecotone_time"]))),
            "eip4844TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "ecotone_time"]))),
            "eip5656TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "ecotone_time"]))),
            "eip6780TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "ecotone_time"]))),
            "rip7212TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "fjord_time"]))),
            "opGraniteTransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "granite_time"]))),
            "opHoloceneTransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "holocene_time"]))),
            "opIsthmusTransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "isthmus_time"]))),
            "eip7702TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "isthmus_time"]))),
            "eip2537TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "isthmus_time"]))),
            "eip2935TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "isthmus_time"]))),
            "eip7623TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "isthmus_time"]))),
            "eip6110TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "isthmus_time"]))),
            "eip7685TransitionTimestamp": fmap(hex, (lookup(config, ["hardforks", "isthmus_time"]))),
            "depositContractAddress": lookup(constants, ["DepositContractAddress", l1]),
            "terminalTotalDifficulty": "0x0",
        }),
        "genesis": filter_none(
            {
                "seal": {
                    "ethereum": {
                        "nonce": lookup(genesis, ["nonce"]),
                        "mixHash": lookup(genesis, ["mixHash"]),
                    }
                },
                "number": lookup(genesis, ["number"]),
                "difficulty": lookup(genesis, ["difficulty"]),
                "author": one_of(lookup(genesis, ["author"]), lookup(genesis, ["coinbase"])),
                "timestamp": lookup(genesis, ["timestamp"]),
                "parentHash": lookup(genesis, ["parentHash"]),
                "extraData": lookup(genesis, ["extraData"]),
                "gasLimit": lookup(genesis, ["gasLimit"]),
                "baseFeePerGas": lookup(genesis, ["baseFeePerGas"]),
                "stateRoot": genesis.get("stateHash"),
            }
        ),
        "nodes": [
            # From: https://github.com/ethereum-optimism/op-geth/blob/3d7afdc2701b74c5987e31521e2c336c4511afdf/params/bootnodes.go
            "enode://d860a01f9722d78051619d1e2351aba3f43f943f6f00718d1b9baa4101932a1f5011f16bb2b1bb35db20d6fe28fa0bf09636d26a87d31de9ec6203eeedb1f666@18.138.108.67:30303",
            "enode://22a8232c3abc76a16ae9d6c3b164f98775fe226f0917b0ca871128a74a8e9630b458460865bab457221f1d448dd9791d24c4e5d88786180ac185df813a68d4de@3.209.45.79:30303",
            "enode://2b252ab6a1d0f971d9722cb839a42cb81db019ba44c08754628ab4a823487071b5695317c8ccd085219c3a03af063495b2f1da8d18218da2d6a82981b45e6ffc@65.108.70.101:30303",
            "enode://4aeb4ab6c14b23e2c4cfdce879c04b0748a20d8e9b59e25ded2a08143e265c6c25936e74cbc8e641e3312ca288673d91f2f93f8e277de3cfa444ecdaaf982052@157.90.35.166:30303",
            "enode://ac906289e4b7f12df423d654c5a962b6ebe5b3a74cc9e06292a85221f9a64a6f1cfdd6b714ed6dacef51578f92b34c60ee91e9ede9c7f8fadc4d347326d95e2b@146.190.13.128:30303",
            "enode://a3435a0155a3e837c02f5e7f5662a2f1fbc25b48e4dc232016e1c51b544cb5b4510ef633ea3278c0e970fa8ad8141e2d4d0f9f95456c537ff05fdf9b31c15072@178.128.136.233:30303",
            "enode://4e5e92199ee224a01932a377160aa432f31d0b351f84ab413a8e0a42f4f36476f8fb1cbe914af0d9aef0d51665c214cf653c651c4bbd9d5550a934f241f1682b@138.197.51.181:30303",
            "enode://143e11fb766781d22d92a2e33f8f104cddae4411a122295ed1fdb6638de96a6ce65f5b7c964ba3763bba27961738fef7d3ecc739268f3e5e771fb4c87b6234ba@146.190.1.103:30303",
            "enode://8b61dc2d06c3f96fddcbebb0efb29d60d3598650275dc469c22229d3e5620369b0d3dedafd929835fe7f489618f19f456fe7c0df572bf2d914a9f4e006f783a9@170.64.250.88:30303",
            "enode://10d62eff032205fcef19497f35ca8477bea0eadfff6d769a147e895d8b2b8f8ae6341630c645c30f5df6e67547c03494ced3d9c5764e8622a26587b083b028e8@139.59.49.206:30303",
            "enode://9e9492e2e8836114cc75f5b929784f4f46c324ad01daf87d956f98b3b6c5fcba95524d6e5cf9861dc96a2c8a171ea7105bb554a197455058de185fa870970c7c@138.68.123.152:30303",
            "enode://ca2774c3c401325850b2477fd7d0f27911efbf79b1e8b335066516e2bd8c4c9e0ba9696a94b1cb030a88eac582305ff55e905e64fb77fe0edcd70a4e5296d3ec@34.65.175.185:30305",
            "enode://dd751a9ef8912be1bfa7a5e34e2c3785cc5253110bd929f385e07ba7ac19929fb0e0c5d93f77827291f4da02b2232240fbc47ea7ce04c46e333e452f8656b667@34.65.107.0:30305",
            "enode://c5d289b56a77b6a2342ca29956dfd07aadf45364dde8ab20d1dc4efd4d1bc6b4655d902501daea308f4d8950737a4e93a4dfedd17b49cd5760ffd127837ca965@34.65.202.239:30305",
            "enode://87a32fd13bd596b2ffca97020e31aef4ddcc1bbd4b95bb633d16c1329f654f34049ed240a36b449fda5e5225d70fe40bc667f53c304b71f8e68fc9d448690b51@3.231.138.188:30301",
            "enode://ca21ea8f176adb2e229ce2d700830c844af0ea941a1d8152a9513b966fe525e809c3a6c73a2c18a12b74ed6ec4380edf91662778fe0b79f6a591236e49e176f9@184.72.129.189:30301",
            "enode://acf4507a211ba7c1e52cdf4eef62cdc3c32e7c9c47998954f7ba024026f9a6b2150cd3f0b734d9c78e507ab70d59ba61dfe5c45e1078c7ad0775fb251d7735a2@3.220.145.177:30301",
            "enode://8a5a5006159bf079d06a04e5eceab2a1ce6e0f721875b2a9c96905336219dbe14203d38f70f3754686a6324f786c2f9852d8c0dd3adac2d080f4db35efc678c5@3.231.11.52:30301",
            "enode://cdadbe835308ad3557f9a1de8db411da1a260a98f8421d62da90e71da66e55e98aaa8e90aa7ce01b408a54e4bd2253d701218081ded3dbe5efbbc7b41d7cef79@54.198.153.150:30301",
            "enode://2bd2e657bb3c8efffb8ff6db9071d9eb7be70d7c6d7d980ff80fc93b2629675c5f750bc0a5ef27cd788c2e491b8795a7e9a4a6e72178c14acc6753c0e5d77ae4@34.65.205.244:30305",
            "enode://db8e1cab24624cc62fc35dbb9e481b88a9ef0116114cd6e41034c55b5b4f18755983819252333509bd8e25f6b12aadd6465710cd2e956558faf17672cce7551f@34.65.173.88:30305",
            "enode://bfda2e0110cfd0f4c9f7aa5bf5ec66e6bd18f71a2db028d36b8bf8b0d6fdb03125c1606a6017b31311d96a36f5ef7e1ad11604d7a166745e6075a715dfa67f8a@34.65.229.245:30305",
            "enode://548f715f3fc388a7c917ba644a2f16270f1ede48a5d88a4d14ea287cc916068363f3092e39936f1a3e7885198bef0e5af951f1d7b1041ce8ba4010917777e71f@18.210.176.114:30301",
            "enode://6f10052847a966a725c9f4adf6716f9141155b99a0fb487fea3f51498f4c2a2cb8d534e680ee678f9447db85b93ff7c74562762c3714783a7233ac448603b25f@107.21.251.55:30301",
            "enode://b1a743328188dba3b2ed8c06abbb2688fabe64a3251e43bd77d4e5265bbd5cf03eca8ace4cde8ddb0c49c409b90bf941ebf556094638c6203edd6baa5ef0091b@3.134.214.169:30303",
            "enode://ea9eaaf695facbe53090beb7a5b0411a81459bbf6e6caac151e587ee77120a1b07f3b9f3a9550f797d73d69840a643b775fd1e40344dea11e7660b6a483fe80e@52.14.30.39:30303",
            "enode://77b6b1e72984d5d50e00ae934ffea982902226fe92fa50da42334c2750d8e405b55a5baabeb988c88125368142a64eda5096d0d4522d3b6eef75d166c7d303a9@3.148.100.173:30303",
            "enode://9e138a8ec4291c4f2fe5851aaee44fc73ae67da87fb26b75e3b94183c7ffc15b2795afc816b0aa084151b95b3a3553f1cd0d1e9dd134dcf059a84d4e0b429afc@3.146.117.118:30303",
            "enode://34d87d649e5c58a17a43c1d59900a2020bd82d5b12ea39467c3366bee2946aaa9c759c77ede61089624691291fb2129eeb2a47687b50e2463188c78e1f738cf2@52.15.54.8:30303",
            "enode://c2405194166fe2c0e6c61ee469745fed1a6802f51c8fc39e1c78c21c9a6a15a7c55304f09ee37e430da9a1ce8117ca085263c6b0f474f6946811e398347611ef@3.146.213.65:30303",
        ],
        "accounts": to_nethermind_accounts(genesis),
    }
    # Post-processing

    ## Optimism specific
    if chain_name == "op" and l1 == "mainnet":
        nethermind["genesis"]["stateUnavailable"] = True
        nethermind["params"]["terminalTotalDifficulty"] = 210470125
        nethermind["params"]["eip2565Transition"] = "0x3C45B0"
        nethermind["params"]["eip2929Transition"] = "0x3C45B0"
        nethermind["params"]["eip2930Transition"] = "0x3C45B0"

    return nethermind


def to_nethermind_runner(chain_name, l1, chain):
    qualified_name = f"{chain_name}-{l1}"
    runner = {
        "$schema": "https://raw.githubusercontent.com/NethermindEth/core-scripts/refs/heads/main/schemas/config.json",
        "Init": {
            "ChainSpecPath": f"chainspec/{qualified_name}.json.zst",
            "GenesisHash": lookup(chain, ["genesis", "l2", "hash"]),
            "BaseDbPath": f"nethermind_db/{qualified_name}",
            "LogFileName": f"{qualified_name}.log",
        },
        "TxPool": {"BlobsSupport": "Disabled"},
        "Sync": {"FastSync": True, "SnapSync": True, "FastSyncCatchUpHeightDelta": "10000000000"},
        "Discovery": {"DiscoveryVersion": "V5"},
        "JsonRpc": {"Enabled": True, "Port": 8545, "EnginePort": 8551},
        "Pruning": {"PruningBoundary": 192},
        "Blocks": {"SecondsPerSlot": lookup(chain, ["block_time"])},
        "Merge": {"Enabled": True},
        "Optimism": {"SequencerUrl": lookup(chain, ["sequencer_rpc"])},
    }
    # Post processing

    ## Optimism specific
    if chain_name == "op" and l1 == "mainnet":
        # For some reason we need to hardcode the genesis hash. See https://github.com/NethermindEth/nethermind/pull/8105/files#r1944390018
        runner["Init"]["GenesisHash"] = "0x7ca38a1916c42007829c55e69d3e9a73265554b586a499015373241b8a3fa48b"
        runner["Sync"]["AncientBodiesBarrier"] = 105235063
        runner["Sync"]["AncientReceiptsBarrier"] = 105235063

    return runner


def main(tmp_dir, output_dir):
    logging.debug("Setting up directories")
    os.makedirs(output_dir, exist_ok=True)
    os.makedirs(path.join(output_dir, "chainspec"), exist_ok=True)
    os.makedirs(path.join(output_dir, "runner"), exist_ok=True)

    logging.debug("Downloading Superchain registry")
    with urlopen(SUPERCHAIN_REPOSITORY) as zip_response:
        with ZipFile(BytesIO(zip_response.read())) as zip_file:
            zip_file.extractall(path=tmp_dir)

    logging.debug("Loading Superchain registry index")
    with open(path.join(tmp_dir, "superchain-registry-main", "chainList.toml"), "rb") as json_config:
        chainList = tomllib.load(json_config)
    with open(path.join(tmp_dir, "superchain-registry-main", "superchain", "extra", "dictionary"), "rb") as json_config:
        superchain_dict = zstd.ZstdCompressionDict(json_config.read())
        zdecompressor = zstd.ZstdDecompressor(dict_data=superchain_dict)

    for chain in chainList["chains"]:
        [l1, chainName] = chain["identifier"].split("/")
        if chainName in IGNORED_CHAINS or l1 in IGNORED_L1S:
            logging.info(f"Ignoring `{l1}-{chainName}`")
            continue

        logging.debug(f"Processing `{l1}-{chainName}`")
        superchain_path = path.join(tmp_dir, "superchain-registry-main", "superchain", "configs", l1, "superchain.toml")
        config_path = path.join(tmp_dir, "superchain-registry-main", "superchain", "configs", l1, f"{chainName}.toml")
        genesis_path = path.join(
            tmp_dir,
            "superchain-registry-main",
            "superchain",
            "extra",
            "genesis",
            l1,
            f"{chainName}.json.zst",
        )

        with (
            open(superchain_path, "rb") as superchain_file,
            open(config_path, "rb") as config_file,
            open(genesis_path, "rb") as genesis_file,
            BytesIO() as genesis_buffer,
        ):
            superchain = tomllib.load(superchain_file)
            chain = tomllib.load(config_file)

            zdecompressor.copy_stream(genesis_file, genesis_buffer)
            genesis_buffer.flush()
            genesis_buffer.seek(0)
            genesis = json.load(genesis_buffer)

            chainspec = to_nethermind_chainspec(chainName, l1, superchain, chain, genesis)
            with open(path.join(tmp_dir, f"{chainName}-{l1}.json"), "w+") as zstd_file:
                json.dump(chainspec, zstd_file)

            runner = to_nethermind_runner(chainName, l1, chain)
            with open(path.join(output_dir, "runner", f"{chainName}-{l1}.json"), "w+") as f:
                json.dump(runner, f, indent=2)

    logging.debug("Training compression dictionary")
    samples = []
    for file in os.listdir(tmp_dir):
        if not file.endswith(".json"):
            continue

        with open(path.join(tmp_dir, file), "rb") as json_config:
            samples.append(json_config.read())
    nethermind_dict = zstd.train_dictionary(2**16, samples, threads=-1)
    zcompressor = zstd.ZstdCompressor(dict_data=nethermind_dict)

    logging.debug("Compressing chainspec files")
    for file in os.listdir(tmp_dir):
        if not file.endswith(".json"):
            continue

        logging.debug(f"Compressing `{file}`")
        with (
            open(path.join(tmp_dir, file), "rb") as json_config,
            open(path.join(output_dir, "chainspec", f"{file}.zst"), "wb+") as zstd_file,
        ):
            zcompressor.copy_stream(json_config, zstd_file)

    logging.debug("Storing compression dictionary")
    with open(path.join(output_dir, "chainspec", "dictionary"), "wb+") as nethermind_dict_file:
        nethermind_dict_file.write(nethermind_dict.as_bytes())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Optimism Superchain to Nethermind chain configuration converter")
    parser.add_argument("-v", "--verbose", action="store_true", help="enable verbose mode")
    parser.add_argument("-o", "--output", default="output", help="output directory for the generated configurations")
    args = parser.parse_args()

    logging.basicConfig(format="[%(levelname)s] %(message)s", level=logging.DEBUG if args.verbose else logging.INFO)

    with tempfile.TemporaryDirectory() as tmp_dir:
        main(tmp_dir, args.output)
