# Compiled Files

## Qemu binaries

```sh
sudo apt install -y qemu-user-static
```

then copied from /usr/bin to here.

## librocksdb-5.15.10.so

use arm/rocksdb to produce

```sh
docker build -f arm/rocksdb -t nethermind/rocksdb
```

!!! compilation takes much time, 
!!! falsely created .so can lead to "file not found", despite file beeing there
!!! do not attempt to change without enough time-budget 



## TODO

* fetch those binaries via docker
* test on major docker host platforms
* !!! could induce problems, start only with enough time-budget !!!
