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

# fetch the published docker image and run it
sudo docker run -it -e NETHERMIND_CONFIG=goerli nethermind/nethermind:arm32 
```

### Create Docker Images


This works currently on usual machines (PC, laptop, CI-server), non-arm:

```sh

git clone https://github.com/NethermindEth/nethermind.git
cd nethermind

# NET 2.2
docker build -f arm/net2v2-arm32v7 -t nethermind/nethermind:arm32 .

#NET 3.0 (not functional yet)
docker build -f arm/net3v0-arm64v8 -t nethermind/nethermind-net3v0-arm64v8 .

```

### Run Docker Containers

```sh
docker run -dit -e NETHERMIND_CONFIG=goerli nethermind/nethermind:arm32  --name neth-goerli
docker attach neth-goerli
docker stop neth-goerli
docker start neth-goerli
docker rm neth-goerli       # delete the container
```

Nethermind Goerli should be synching now.

### Troubleshooting

#### QEMU

Possibly needed: install and register qemu on your machine

```sh
docker run --rm --privileged multiarch/qemu-user-static:register --reset
```

#### Build on Pi

Cannot `docker build` on the pi itself:

dockerfiles are for "usual" machines (pc, laptop, CI-server).

look into arm/net2v2-arm32v7 for possible hints to build on arm itself.

