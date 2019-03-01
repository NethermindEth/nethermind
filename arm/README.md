# Nethermind Goerli ARM Architecture

## Raspberry Pi3B(+)

Prepare a fixed ethernet connection, USB-keyboard, HDMI-monitor

* Visit: http://cdimage.ubuntu.com/releases/18.04/release/
  * Get the [arm64 pi3 image](http://cdimage.ubuntu.com/releases/18.04/release/ubuntu-18.04.2-preinstalled-server-arm64+raspi3.img.xz)

* Visti: https://www.balena.io/etcher/
  * Get you relevant flasher (dropdown)
* Flash the SD/USB drive
* insert the medium to your pi and boot it.

```sh
# user/pass: ubuntu/ubuntu
sudo apt install docker.io git
```

### Manually Build rocksdb

This step takes quite long:

```sh
docker build -f arm/rockdb -t nethermind/rocksdb
docker cp nethermind/rocksdb:/rocksdb/librocksdb.so arm/lib/librocksdb-5.15.10.so
```

(Possible Enhancement would be to use a cross-architecture-compilation)

### Create Docker Images


```sh

git clone https://github.com/NethermindEth/nethermind.git
cd nethermind

# NET 2.2
docker build -f arm/net2v2-arm32v7 -t nethermind/nethermind-net2v2-arm32v7 .

#NET 3.0 (not functional yet)
docker build -f arm/net3v0-arm64v8 -t nethermind/nethermind-net2v2-arm32v7 .

```

### Run Docker Images

```sh
docker run -it nethermind/nethermind-net2v2-arm32v7
```

Nethermind Goerli should be synching now.

### Trouble

install and register qemu on your machine

```sh

docker run --rm --privileged multiarch/qemu-user-static:register --reset
```