from io import BytesIO
from urllib.request import urlopen
from zipfile import ZipFile

SUPERCHAIN_REPOSITORY = "https://github.com/ethereum-optimism/superchain-registry/archive/refs/heads/main.zip"

with urlopen(SUPERCHAIN_REPOSITORY) as zipresp:
    with ZipFile(BytesIO(zipresp.read())) as zfile:
        zfile.extractall()

print("Done")
