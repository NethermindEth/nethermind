// SPDX-License-Identifier: GPL-3.0

pragma solidity >=0.7.0 <0.8.0;

/**
 * @title Storage
 * @dev Store & retrieve value in a variable
 */
contract BadContract {

    uint256 number;
    
    function divide() public view returns (uint256){
        return 3/number;
    }
}