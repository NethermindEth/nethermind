// SPDX-License-Identifier: GPL-3.0
pragma solidity >=0.7.0 <0.9.0;

contract Setable {
    bytes32 hash;
    
    constructor() {
        hash = keccak256(abi.encode(0)); 
    }
    
    function set(uint256 _number) public {
        hash = keccak256(abi.encode(_number, hash));
    }
    
    function get() public view returns(bytes32) {
        return hash;
    }
}