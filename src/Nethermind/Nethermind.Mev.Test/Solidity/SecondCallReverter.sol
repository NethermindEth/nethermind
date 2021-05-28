// SPDX-License-Identifier: GPL-3.0
pragma solidity >=0.7.0 <0.9.0;

contract SecondCallReverter {
    
    bool fail;
    
    constructor() {
        fail = false;
    }
    
    function failOnSecondCall() public {
        if (fail == false) {
            fail = true;
        }
        else if (fail == true) {
            revert();
        }
    }
}