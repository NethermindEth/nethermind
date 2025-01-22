import tomllib
import json
import sha3
import zstandard as zstd
from functools import reduce


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


with open("superchain.toml", "rb") as f:
    superchain = tomllib.load(f)

L1_CHAIN = "mainnet"
CHAIN_NAME = "worldchain"
CHAIN_FILE = f"{CHAIN_NAME}.toml"
GENESIS_FILE = f"{CHAIN_NAME}.json"

with open(CHAIN_FILE, "rb") as f:
    chain = tomllib.load(f)

with open(GENESIS_FILE, "rb") as f:
    genesis = json.load(f)

config = merge_all(superchain, chain)

codeHashes = {}
def map_accounts(alloc):
    result = {}
    for k, v in alloc.items():
        value = v

        if value.get("balance") == "0x0":
            del value["balance"]

        if value.get("code") is not None:
            h = keccak(value["code"])
            value["codeHash"] = h
            codeHashes[h] = value["code"][2:]
            del value["code"]
        else:
            value["codeHash"] = (
                "0x0000000000000000000000000000000000000000000000000000000000000000"
            )

        key = f"0x{k}"
        result[key] = value
    return result


nethermind = {
    "name": lookup(config, ["name"]),
    "dataDir": f"{CHAIN_NAME}-{L1_CHAIN}",
    "engine": {
        "Optimism": {
            "params": {
                "regolithTimestamp": "0x0",
                "bedrockBlockNumber": hex(lookup(config, ["genesis", "l2", "number"])),
                "canyonTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
                "ecotoneTimestamp": hex(lookup(config, ["hardforks", "ecotone_time"])),
                "fjordTimestamp": hex(lookup(config, ["hardforks", "fjord_time"])),
                "graniteTimestamp": hex(lookup(config, ["hardforks", "granite_time"])),
                "holoceneTimestamp": hex(
                    lookup(config, ["hardforks", "holocene_time"])
                ),
                "canyonBaseFeeChangeDenominator": str(
                    lookup(config, ["optimism", "eip1559_denominator_canyon"])
                ),
                "l1FeeRecipient": "0x420000000000000000000000000000000000001A",
                "l1BlockAddress": "0x4200000000000000000000000000000000000015",
                "create2DeployerAddress": "0x13b0D85CcB8bf860b6b79AF3029fCA081AE9beF2",
                "create2DeployerCode": "6080604052600436106100435760003560e01c8063076c37b21461004f578063481286e61461007157806356299481146100ba57806366cfa057146100da57600080fd5b3661004a57005b600080fd5b34801561005b57600080fd5b5061006f61006a366004610327565b6100fa565b005b34801561007d57600080fd5b5061009161008c366004610327565b61014a565b60405173ffffffffffffffffffffffffffffffffffffffff909116815260200160405180910390f35b3480156100c657600080fd5b506100916100d5366004610349565b61015d565b3480156100e657600080fd5b5061006f6100f53660046103ca565b610172565b61014582826040518060200161010f9061031a565b7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe082820381018352601f90910116604052610183565b505050565b600061015683836102e7565b9392505050565b600061016a8484846102f0565b949350505050565b61017d838383610183565b50505050565b6000834710156101f4576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152601d60248201527f437265617465323a20696e73756666696369656e742062616c616e636500000060448201526064015b60405180910390fd5b815160000361025f576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820181905260248201527f437265617465323a2062797465636f6465206c656e677468206973207a65726f60448201526064016101eb565b8282516020840186f5905073ffffffffffffffffffffffffffffffffffffffff8116610156576040517f08c379a000000000000000000000000000000000000000000000000000000000815260206004820152601960248201527f437265617465323a204661696c6564206f6e206465706c6f790000000000000060448201526064016101eb565b60006101568383305b6000604051836040820152846020820152828152600b8101905060ff815360559020949350505050565b61014e806104ad83390190565b6000806040838503121561033a57600080fd5b50508035926020909101359150565b60008060006060848603121561035e57600080fd5b8335925060208401359150604084013573ffffffffffffffffffffffffffffffffffffffff8116811461039057600080fd5b809150509250925092565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052604160045260246000fd5b6000806000606084860312156103df57600080fd5b8335925060208401359150604084013567ffffffffffffffff8082111561040557600080fd5b818601915086601f83011261041957600080fd5b81358181111561042b5761042b61039b565b604051601f82017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe0908116603f011681019083821181831017156104715761047161039b565b8160405282815289602084870101111561048a57600080fd5b826020860160208301376000602084830101528095505050505050925092509256fe608060405234801561001057600080fd5b5061012e806100206000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c8063249cb3fa14602d575b600080fd5b603c603836600460b1565b604e565b60405190815260200160405180910390f35b60008281526020818152604080832073ffffffffffffffffffffffffffffffffffffffff8516845290915281205460ff16608857600060aa565b7fa2ef4600d742022d532d4747cb3547474667d6f13804902513b2ec01c848f4b45b9392505050565b6000806040838503121560c357600080fd5b82359150602083013573ffffffffffffffffffffffffffffffffffffffff8116811460ed57600080fd5b80915050925092905056fea26469706673582212205ffd4e6cede7d06a5daf93d48d0541fc68189eeb16608c1999a82063b666eb1164736f6c63430008130033a2646970667358221220fdc4a0fe96e3b21c108ca155438d37c9143fb01278a3c1d274948bad89c564ba64736f6c63430008130033",
            }
        },
    },
    "params": {
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
        "eip1559FeeCollectorTransition": hex(
            lookup(config, ["genesis", "l2", "number"])
        ),
        "feeCollector": "0x4200000000000000000000000000000000000019",
        "eip1559ElasticityMultiplier": hex(
            lookup(config, ["optimism", "eip1559_elasticity"])
        ),
        "eip1559BaseFeeMaxChangeDenominator": hex(
            lookup(config, ["optimism", "eip1559_denominator"])
        ),
        "eip3198Transition": hex(lookup(config, ["genesis", "l2", "number"])),
        "eip3529Transition": hex(lookup(config, ["genesis", "l2", "number"])),
        "eip3541Transition": hex(lookup(config, ["genesis", "l2", "number"])),
        "eip4895TransitionTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
        "eip3651TransitionTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
        "eip3855TransitionTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
        "eip3860TransitionTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
        "eip1153TransitionTimestamp": hex(
            lookup(config, ["hardforks", "ecotone_time"])
        ),
        "eip4788TransitionTimestamp": hex(
            lookup(config, ["hardforks", "ecotone_time"])
        ),
        "eip4844TransitionTimestamp": hex(
            lookup(config, ["hardforks", "ecotone_time"])
        ),
        "eip5656TransitionTimestamp": hex(
            lookup(config, ["hardforks", "ecotone_time"])
        ),
        "eip6780TransitionTimestamp": hex(
            lookup(config, ["hardforks", "ecotone_time"])
        ),
        "rip7212TransitionTimestamp": hex(lookup(config, ["hardforks", "fjord_time"])),
        "opGraniteTransitionTimestamp": hex(
            lookup(config, ["hardforks", "granite_time"])
        ),
        "opHoloceneTransitionTimestamp": hex(
            lookup(config, ["hardforks", "holocene_time"])
        ),
        "terminalTotalDifficulty": "0x0",
    },
    "genesis": {
        "seal": {
            "ethereum": {
                "nonce": lookup(genesis, ["nonce"]),
                "mixHash": lookup(genesis, ["mixHash"]),
            }
        },
        "number": lookup(genesis, ["number"]),
        "difficulty": lookup(genesis, ["difficulty"]),
        "author": lookup(genesis, ["author"]),
        "timestamp": lookup(genesis, ["timestamp"]),
        "parentHash": lookup(genesis, ["parentHash"]),
        "extraData": lookup(genesis, ["extraData"]),
        "stateUnavailable": lookup(genesis, ["stateUnavailable"]),
        "gasLimit": lookup(genesis, ["gasLimit"]),
        "baseFeePerGas": lookup(genesis, ["baseFeePerGas"]),
        "stateRoot": genesis.get("stateHash"),
    },
    "nodes": [
        "enode://87a32fd13bd596b2ffca97020e31aef4ddcc1bbd4b95bb633d16c1329f654f34049ed240a36b449fda5e5225d70fe40bc667f53c304b71f8e68fc9d448690b51@3.231.138.188:30301",
        "enode://ca21ea8f176adb2e229ce2d700830c844af0ea941a1d8152a9513b966fe525e809c3a6c73a2c18a12b74ed6ec4380edf91662778fe0b79f6a591236e49e176f9@184.72.129.189:30301",
        "enode://acf4507a211ba7c1e52cdf4eef62cdc3c32e7c9c47998954f7ba024026f9a6b2150cd3f0b734d9c78e507ab70d59ba61dfe5c45e1078c7ad0775fb251d7735a2@3.220.145.177:30301",
        "enode://8a5a5006159bf079d06a04e5eceab2a1ce6e0f721875b2a9c96905336219dbe14203d38f70f3754686a6324f786c2f9852d8c0dd3adac2d080f4db35efc678c5@3.231.11.52:30301",
        "enode://cdadbe835308ad3557f9a1de8db411da1a260a98f8421d62da90e71da66e55e98aaa8e90aa7ce01b408a54e4bd2253d701218081ded3dbe5efbbc7b41d7cef79@54.198.153.150:30301",
        "enode://ca2774c3c401325850b2477fd7d0f27911efbf79b1e8b335066516e2bd8c4c9e0ba9696a94b1cb030a88eac582305ff55e905e64fb77fe0edcd70a4e5296d3ec@34.65.175.185:30305",
        "enode://dd751a9ef8912be1bfa7a5e34e2c3785cc5253110bd929f385e07ba7ac19929fb0e0c5d93f77827291f4da02b2232240fbc47ea7ce04c46e333e452f8656b667@34.65.107.0:30305",
        "enode://c5d289b56a77b6a2342ca29956dfd07aadf45364dde8ab20d1dc4efd4d1bc6b4655d902501daea308f4d8950737a4e93a4dfedd17b49cd5760ffd127837ca965@34.65.202.239:30305",
        "enode://87a32fd13bd596b2ffca97020e31aef4ddcc1bbd4b95bb633d16c1329f654f34049ed240a36b449fda5e5225d70fe40bc667f53c304b71f8e68fc9d448690b51@3.231.138.188:30301",
        "enode://ca21ea8f176adb2e229ce2d700830c844af0ea941a1d8152a9513b966fe525e809c3a6c73a2c18a12b74ed6ec4380edf91662778fe0b79f6a591236e49e176f9@184.72.129.189:30301",
        "enode://acf4507a211ba7c1e52cdf4eef62cdc3c32e7c9c47998954f7ba024026f9a6b2150cd3f0b734d9c78e507ab70d59ba61dfe5c45e1078c7ad0775fb251d7735a2@3.220.145.177:30301",
        "enode://8a5a5006159bf079d06a04e5eceab2a1ce6e0f721875b2a9c96905336219dbe14203d38f70f3754686a6324f786c2f9852d8c0dd3adac2d080f4db35efc678c5@3.231.11.52:30301",
        "enode://cdadbe835308ad3557f9a1de8db411da1a260a98f8421d62da90e71da66e55e98aaa8e90aa7ce01b408a54e4bd2253d701218081ded3dbe5efbbc7b41d7cef79@54.198.153.150:30301",
    ],
}

# Post-processing

## Genesis
nethermind["genesis"] = {
    k: v for k, v in nethermind["genesis"].items() if v is not None
}

## Alloc/Accounts
alloc = lookup(genesis, ["alloc"])
if alloc is not None:
    nethermind["accounts"] = map_accounts(alloc)
    nethermind["codeHashes"] = codeHashes

## Optimism
if CHAIN_NAME == "op":
    nethermind["params"]["terminalTotalDifficulty"] = 210470125
    nethermind["params"]["eip2565Transition"] = "0x3C45B0"
    nethermind["params"]["eip2929Transition"] = "0x3C45B0"
    nethermind["params"]["eip2930Transition"] = "0x3C45B0"

print(json.dumps(nethermind, indent=2))
