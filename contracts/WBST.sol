// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

/**
 * @title WBST (Wrapped Basalt Token)
 * @notice ERC-20 representation of BST on Ethereum.
 *         Minted 1:1 when BST is locked on Basalt; burned when withdrawn back.
 *         Only the bridge contract can mint/burn.
 */
contract WBST {
    string public constant name = "Wrapped Basalt Token";
    string public constant symbol = "wBST";
    uint8 public constant decimals = 18;

    uint256 public totalSupply;
    mapping(address => uint256) public balanceOf;
    mapping(address => mapping(address => uint256)) public allowance;

    address public bridge;

    event Transfer(address indexed from, address indexed to, uint256 value);
    event Approval(address indexed owner, address indexed spender, uint256 value);

    modifier onlyBridge() {
        require(msg.sender == bridge, "Only bridge");
        _;
    }

    constructor(address _bridge) {
        bridge = _bridge;
    }

    function transfer(address to, uint256 amount) external returns (bool) {
        require(balanceOf[msg.sender] >= amount, "Insufficient balance");
        balanceOf[msg.sender] -= amount;
        balanceOf[to] += amount;
        emit Transfer(msg.sender, to, amount);
        return true;
    }

    function approve(address spender, uint256 amount) external returns (bool) {
        allowance[msg.sender][spender] = amount;
        emit Approval(msg.sender, spender, amount);
        return true;
    }

    function transferFrom(address from, address to, uint256 amount) external returns (bool) {
        require(balanceOf[from] >= amount, "Insufficient balance");
        require(allowance[from][msg.sender] >= amount, "Insufficient allowance");
        allowance[from][msg.sender] -= amount;
        balanceOf[from] -= amount;
        balanceOf[to] += amount;
        emit Transfer(from, to, amount);
        return true;
    }

    /**
     * @notice Mint wBST — only callable by the bridge contract.
     */
    function mint(address to, uint256 amount) external onlyBridge {
        totalSupply += amount;
        balanceOf[to] += amount;
        emit Transfer(address(0), to, amount);
    }

    /**
     * @notice Burn wBST — only callable by the bridge contract.
     */
    function burn(address from, uint256 amount) external onlyBridge {
        require(balanceOf[from] >= amount, "Insufficient balance");
        balanceOf[from] -= amount;
        totalSupply -= amount;
        emit Transfer(from, address(0), amount);
    }
}
