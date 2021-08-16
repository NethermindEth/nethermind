// SPDX-License-Identifier: GPL-3.0
pragma solidity >=0.7.0 <0.9.0;

contract Looper {
    constructor() {}
    
    function loop(uint times) pure public {
        uint counter = 0;
        for(uint i = 0; i < times; i++) {
            counter += i*2;
        }
    }
}