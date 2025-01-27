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


def to_nethermind_chainspec(chain_name, l1, superchain, chain, genesis):
    config = merge_all(superchain, chain)

    nethermind = {
        "name": lookup(config, ["name"]),
        "dataDir": f"{chain_name}-{l1}",
        "engine": {
            "Optimism": {
                "params": {
                    "regolithTimestamp": "0x0",
                    "bedrockBlockNumber": hex(lookup(config, ["genesis", "l2", "number"])),
                    "canyonTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
                    "ecotoneTimestamp": hex(lookup(config, ["hardforks", "ecotone_time"])),
                    "fjordTimestamp": hex(lookup(config, ["hardforks", "fjord_time"])),
                    "graniteTimestamp": hex(lookup(config, ["hardforks", "granite_time"])),
                    "holoceneTimestamp": hex(lookup(config, ["hardforks", "holocene_time"])),
                    "canyonBaseFeeChangeDenominator": str(lookup(config, ["optimism", "eip1559_denominator_canyon"])),
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
            "eip1559FeeCollectorTransition": hex(lookup(config, ["genesis", "l2", "number"])),
            "feeCollector": "0x4200000000000000000000000000000000000019",
            "eip1559ElasticityMultiplier": hex(lookup(config, ["optimism", "eip1559_elasticity"])),
            "eip1559BaseFeeMaxChangeDenominator": hex(lookup(config, ["optimism", "eip1559_denominator"])),
            "eip3198Transition": hex(lookup(config, ["genesis", "l2", "number"])),
            "eip3529Transition": hex(lookup(config, ["genesis", "l2", "number"])),
            "eip3541Transition": hex(lookup(config, ["genesis", "l2", "number"])),
            "eip4895TransitionTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
            "eip3651TransitionTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
            "eip3855TransitionTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
            "eip3860TransitionTimestamp": hex(lookup(config, ["hardforks", "canyon_time"])),
            "eip1153TransitionTimestamp": hex(lookup(config, ["hardforks", "ecotone_time"])),
            "eip4788TransitionTimestamp": hex(lookup(config, ["hardforks", "ecotone_time"])),
            "eip4844TransitionTimestamp": hex(lookup(config, ["hardforks", "ecotone_time"])),
            "eip5656TransitionTimestamp": hex(lookup(config, ["hardforks", "ecotone_time"])),
            "eip6780TransitionTimestamp": hex(lookup(config, ["hardforks", "ecotone_time"])),
            "rip7212TransitionTimestamp": hex(lookup(config, ["hardforks", "fjord_time"])),
            "opGraniteTransitionTimestamp": hex(lookup(config, ["hardforks", "granite_time"])),
            "opHoloceneTransitionTimestamp": hex(lookup(config, ["hardforks", "holocene_time"])),
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
            "enr:-KG4QMOEswP62yzDjSwWS4YEjtTZ5PO6r65CPqYBkgTTkrpaedQ8uEUo1uMALtJIvb2w_WWEVmg5yt1UAuK1ftxUU7QDhGV0aDKQu6TalgMAAAD__________4JpZIJ2NIJpcIQEnfA2iXNlY3AyNTZrMaEDfol8oLr6XJ7FsdAYE7lpJhKMls4G_v6qQOGKJUWGb_uDdGNwgiMog3VkcIIjKA",
            "enr:-KG4QF4B5WrlFcRhUU6dZETwY5ZzAXnA0vGC__L1Kdw602nDZwXSTs5RFXFIFUnbQJmhNGVU6OIX7KVrCSTODsz1tK4DhGV0aDKQu6TalgMAAAD__________4JpZIJ2NIJpcIQExNYEiXNlY3AyNTZrMaECQmM9vp7KhaXhI-nqL_R0ovULLCFSFTa9CPPSdb1zPX6DdGNwgiMog3VkcIIjKA",
            "enr:-Ku4QImhMc1z8yCiNJ1TyUxdcfNucje3BGwEHzodEZUan8PherEo4sF7pPHPSIB1NNuSg5fZy7qFsjmUKs2ea1Whi0EBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpD1pf1CAAAAAP__________gmlkgnY0gmlwhBLf22SJc2VjcDI1NmsxoQOVphkDqal4QzPMksc5wnpuC3gvSC8AfbFOnZY_On34wIN1ZHCCIyg",
            "enr:-Ku4QP2xDnEtUXIjzJ_DhlCRN9SN99RYQPJL92TMlSv7U5C1YnYLjwOQHgZIUXw6c-BvRg2Yc2QsZxxoS_pPRVe0yK8Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpD1pf1CAAAAAP__________gmlkgnY0gmlwhBLf22SJc2VjcDI1NmsxoQMeFF5GrS7UZpAH2Ly84aLK-TyvH-dRo0JM1i8yygH50YN1ZHCCJxA",
            "enr:-Ku4QPp9z1W4tAO8Ber_NQierYaOStqhDqQdOPY3bB3jDgkjcbk6YrEnVYIiCBbTxuar3CzS528d2iE7TdJsrL-dEKoBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpD1pf1CAAAAAP__________gmlkgnY0gmlwhBLf22SJc2VjcDI1NmsxoQMw5fqqkw2hHC4F5HZZDPsNmPdB1Gi8JPQK7pRc9XHh-oN1ZHCCKvg",
            "enr:-Le4QPUXJS2BTORXxyx2Ia-9ae4YqA_JWX3ssj4E_J-3z1A-HmFGrU8BpvpqhNabayXeOZ2Nq_sbeDgtzMJpLLnXFgAChGV0aDKQtTA_KgEAAAAAIgEAAAAAAIJpZIJ2NIJpcISsaa0Zg2lwNpAkAIkHAAAAAPA8kv_-awoTiXNlY3AyNTZrMaEDHAD2JKYevx89W0CcFJFiskdcEzkH_Wdv9iW42qLK79ODdWRwgiMohHVkcDaCI4I",
            "enr:-Le4QLHZDSvkLfqgEo8IWGG96h6mxwe_PsggC20CL3neLBjfXLGAQFOPSltZ7oP6ol54OvaNqO02Rnvb8YmDR274uq8ChGV0aDKQtTA_KgEAAAAAIgEAAAAAAIJpZIJ2NIJpcISLosQxg2lwNpAqAX4AAAAAAPA8kv_-ax65iXNlY3AyNTZrMaEDBJj7_dLFACaxBfaI8KZTh_SSJUjhyAyfshimvSqo22WDdWRwgiMohHVkcDaCI4I",
            "enr:-Le4QH6LQrusDbAHPjU_HcKOuMeXfdEB5NJyXgHWFadfHgiySqeDyusQMvfphdYWOzuSZO9Uq2AMRJR5O4ip7OvVma8BhGV0aDKQtTA_KgEAAAAAIgEAAAAAAIJpZIJ2NIJpcISLY9ncg2lwNpAkAh8AgQIBAAAAAAAAAAmXiXNlY3AyNTZrMaECDYCZTZEksF-kmgPholqgVt8IXr-8L7Nu7YrZ7HUpgxmDdWRwgiMohHVkcDaCI4I",
            "enr:-Le4QIqLuWybHNONr933Lk0dcMmAB5WgvGKRyDihy1wHDIVlNuuztX62W51voT4I8qD34GcTEOTmag1bcdZ_8aaT4NUBhGV0aDKQtTA_KgEAAAAAIgEAAAAAAIJpZIJ2NIJpcISLY04ng2lwNpAkAh8AgAIBAAAAAAAAAA-fiXNlY3AyNTZrMaEDscnRV6n1m-D9ID5UsURk0jsoKNXt1TIrj8uKOGW6iluDdWRwgiMohHVkcDaCI4I",
            "enr:-Ku4QHqVeJ8PPICcWk1vSn_XcSkjOkNiTg6Fmii5j6vUQgvzMc9L1goFnLKgXqBJspJjIsB91LTOleFmyWWrFVATGngBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhAMRHkWJc2VjcDI1NmsxoQKLVXFOhp2uX6jeT0DvvDpPcU8FWMjQdR4wMuORMhpX24N1ZHCCIyg",
            "enr:-Ku4QG-2_Md3sZIAUebGYT6g0SMskIml77l6yR-M_JXc-UdNHCmHQeOiMLbylPejyJsdAPsTHJyjJB2sYGDLe0dn8uYBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhBLY-NyJc2VjcDI1NmsxoQORcM6e19T1T9gi7jxEZjk_sjVLGFscUNqAY9obgZaxbIN1ZHCCIyg",
            "enr:-Ku4QPn5eVhcoF1opaFEvg1b6JNFD2rqVkHQ8HApOKK61OIcIXD127bKWgAtbwI7pnxx6cDyk_nI88TrZKQaGMZj0q0Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhDayLMaJc2VjcDI1NmsxoQK2sBOLGcUb4AwuYzFuAVCaNHA-dy24UuEKkeFNgCVCsIN1ZHCCIyg",
            "enr:-Ku4QEWzdnVtXc2Q0ZVigfCGggOVB2Vc1ZCPEc6j21NIFLODSJbvNaef1g4PxhPwl_3kax86YPheFUSLXPRs98vvYsoBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhDZBrP2Jc2VjcDI1NmsxoQM6jr8Rb1ktLEsVcKAPa08wCsKUmvoQ8khiOl_SLozf9IN1ZHCCIyg",
            "enr:-LK4QA8FfhaAjlb_BXsXxSfiysR7R52Nhi9JBt4F8SPssu8hdE1BXQQEtVDC3qStCW60LSO7hEsVHv5zm8_6Vnjhcn0Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhAN4aBKJc2VjcDI1NmsxoQJerDhsJ-KxZ8sHySMOCmTO6sHM3iCFQ6VMvLTe948MyYN0Y3CCI4yDdWRwgiOM",
            "enr:-LK4QKWrXTpV9T78hNG6s8AM6IO4XH9kFT91uZtFg1GcsJ6dKovDOr1jtAAFPnS2lvNltkOGA9k29BUN7lFh_sjuc9QBh2F0dG5ldHOIAAAAAAAAAACEZXRoMpC1MD8qAAAAAP__________gmlkgnY0gmlwhANAdd-Jc2VjcDI1NmsxoQLQa6ai7y9PMN5hpLe5HmiJSlYzMuzP7ZhwRiwHvqNXdoN0Y3CCI4yDdWRwgiOM",
        ],
        "accounts": lookup(genesis, ["alloc"]),
    }
    # Post-processing

    ## Genesis
    nethermind["genesis"] = {k: v for k, v in nethermind["genesis"].items() if v is not None}

    ## Optimism specific
    if chain_name == "op" and l1 == "mainnet":
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
            "ChainSpecPath": f"chainspec/{qualified_name}.json.zstd",
            "BaseDbPath": f"nethermind_db/{qualified_name}",
            "LogFileName": f"{qualified_name}.logs.txt",
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
        runner["Optimism"]["Snapshot"] = (
            {
                "Enabled": True,
                "DownloadUrl": "http://optimism-snapshot.nethermind.io/op-mainnet-genesis-v1.zip",
                "SnapshotFileName": "op-mainnet-genesis-v1.zip",
                "Checksum": "0xd7e15b26175c4c924acf75c5790e75d5eaa044977ca8e1904dc62d5d0769eba3",
            },
        )

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
            open(path.join(output_dir, "chainspec", f"{file}.zstd"), "wb+") as zstd_file,
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
