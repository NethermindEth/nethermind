// SPDX-License-Identifier: GPL-3.0
pragma solidity >=0.7.0 <0.9.0;

contract Coinbase {
    constructor() {}
    
    receive() external payable {}
    
    function pay() public {
        require(address(this).balance > 0);
        // 0.9.0?
        // payable(block.coinbase).transfer(address(this).balance);
        block.coinbase.transfer(address(this).balance);
    }
}