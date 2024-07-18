// SPDX-License-Identifier: GPL-3.0
pragma solidity >=0.7.0 <0.9.0;

contract Callable {
    uint number;
    
    constructor() {
        number = 10; 
    }
    
    function set() public {
        number = 15;
    }
    
    function get() public view returns(uint) {
        return number;
    }
}