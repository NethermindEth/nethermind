// SPDX-License-Identifier: GPL-3.0
pragma solidity >=0.7.0 <0.9.0;

contract Coinbase {
    constructor() {}
    
    function deposit() public payable {}
    
    function pay() public {
        require(address(this).balance > 0);
        block.coinbase.transfer(address(this).balance);
    }
}