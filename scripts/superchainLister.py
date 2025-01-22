import tomllib
import os.path as path
import zstandard as zstd
import io


with open(path.join("superchain-registry-main", "chainList.toml"), "rb") as f:
  chainList = tomllib.load(f)

with open(path.join("superchain-registry-main", "superchain", "extra", "dictionary"), "rb") as f:
  zstd_dict = zstd.ZstdCompressionDict(f.read())

  zdecompressor = zstd.ZstdDecompressor(dict_data=zstd_dict)

ingored_chains = ["arena-z-testnet", "creator-chain-testnet"]
ignored_l1 = ["sepolia-dev-0"]

for chain in chainList["chains"]:
  [l1, chainName] = chain["identifier"].split("/")
  if chainName in ingored_chains or l1 in ignored_l1:
    continue

  config_path = path.join("superchain-registry-main", "superchain", "configs", l1, f"{chainName}.toml")
  genesis_path = path.join("superchain-registry-main", "superchain", "extra", "genesis", l1, f"{chainName}.json.zst")

  with open(genesis_path, 'rb') as ifh, io.StringIO() as ofh:
    zdecompressor.copy_stream(ifh, ofh)
