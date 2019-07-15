let sokol = "http://3.85.253.242:8545"
let local = "http://localhost:8545"
node.switch(sokol)
let val0 = "0x8bf38d4764929064f2d4d3a56520a76ab3df415b"
let val1 = "0xe8ddc5c7a2d2f0d7a9798459c0104fdf5e987aca"

let pre = [
  "0x0000000000000000000000000000000000000001",
  "0x0000000000000000000000000000000000000002",
  "0x0000000000000000000000000000000000000003",
  "0x0000000000000000000000000000000000000004",
  "0x0000000000000000000000000000000000000005",
  "0x0000000000000000000000000000000000000006",
  "0x0000000000000000000000000000000000000007",
  "0x0000000000000000000000000000000000000008"]


var hexChar = ["0", "1", "2", "3", "4", "5", "6", "7","8", "9", "A", "B", "C", "D", "E", "F"];

function byteToHex(b) {
  return hexChar[(b >> 4) & 0x0f] + hexChar[b & 0x0f];
}

function checkSokol() {
  serialize(eth.getBlockByNumber("0x0"))

  log(val0)
  log(eth.getTransactionCount(val0, "0x0"))
  log(eth.getBalance(val0, "0x0"))
  
  log(val1)
  log(eth.getTransactionCount(val1, "0x0"))
  log(eth.getBalance(val1, "0x0"))
  
  pre.forEach(function(element) {
    log(element)
    log(eth.getTransactionCount(element, "0x0"))
    log(eth.getBalance(element, "0x0"))
  });
}