// SPDX-License-Identifier: GPL-3.0
pragma solidity >=0.7.0 <0.9.0;

contract CustomizableCoinbasePayer {
    
    uint256 coinbasePayment;
    
    constructor() {
        coinbasePayment = 100_000_000;
    }
    
    function deposit() public payable {}
    
    function changeCoinbasePayment(uint256 newCoinbasePayment) public {
        coinbasePayment = newCoinbasePayment;
    }
    
    function payCoinbase() public {
        block.coinbase.transfer(coinbasePayment);
    }
}