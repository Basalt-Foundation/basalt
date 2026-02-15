// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import "./WBST.sol";

/**
 * @title BasaltBridge
 * @notice Bridge contract deployed on Ethereum (Sepolia testnet).
 *         Manages wBST minting (for deposits from Basalt) and burning (for withdrawals to Basalt).
 *         Uses a multisig relayer model for Phase 2; trustless light client planned for Phase 3.
 */
contract BasaltBridge {
    WBST public immutable wbst;

    // Multisig relayers
    mapping(address => bool) public relayers;
    uint256 public relayerCount;
    uint256 public threshold;

    // Processed deposits (replay protection)
    mapping(uint256 => bool) public processedDeposits;

    // Withdrawal nonce (for Basalt -> Ethereum)
    uint256 public withdrawalNonce;

    // Events
    event DepositProcessed(uint256 indexed nonce, address indexed recipient, uint256 amount);
    event WithdrawalInitiated(uint256 indexed nonce, address indexed sender, bytes20 basaltRecipient, uint256 amount);
    event RelayerAdded(address indexed relayer);
    event RelayerRemoved(address indexed relayer);
    event ThresholdChanged(uint256 newThreshold);

    modifier onlyRelayer() {
        require(relayers[msg.sender], "Not a relayer");
        _;
    }

    constructor(address _wbst, address[] memory _relayers, uint256 _threshold) {
        require(_threshold > 0 && _threshold <= _relayers.length, "Invalid threshold");

        wbst = WBST(_wbst);
        threshold = _threshold;

        for (uint256 i = 0; i < _relayers.length; i++) {
            relayers[_relayers[i]] = true;
        }
        relayerCount = _relayers.length;
    }

    /**
     * @notice Process a deposit from Basalt — mint wBST to the recipient.
     *         Called by relayers with a multisig attestation.
     * @param nonce Deposit nonce from Basalt
     * @param recipient Ethereum address to receive wBST
     * @param amount Amount of wBST to mint (1:1 with locked BST)
     * @param signatures Relayer signatures (abi.encodePacked(v, r, s) for each)
     */
    function processDeposit(
        uint256 nonce,
        address recipient,
        uint256 amount,
        bytes[] calldata signatures
    ) external {
        require(!processedDeposits[nonce], "Already processed");
        require(signatures.length >= threshold, "Insufficient signatures");

        // Verify multisig
        bytes32 messageHash = keccak256(abi.encodePacked(nonce, recipient, amount));
        bytes32 ethSignedHash = keccak256(abi.encodePacked("\x19Ethereum Signed Message:\n32", messageHash));

        uint256 validSigs = 0;
        address[] memory seen = new address[](signatures.length);

        for (uint256 i = 0; i < signatures.length; i++) {
            address signer = recoverSigner(ethSignedHash, signatures[i]);
            require(relayers[signer], "Invalid relayer signature");

            // Check for duplicates
            for (uint256 j = 0; j < validSigs; j++) {
                require(seen[j] != signer, "Duplicate signature");
            }
            seen[validSigs] = signer;
            validSigs++;
        }

        require(validSigs >= threshold, "Threshold not met");

        processedDeposits[nonce] = true;
        wbst.mint(recipient, amount);

        emit DepositProcessed(nonce, recipient, amount);
    }

    /**
     * @notice Initiate a withdrawal to Basalt — burn wBST and emit event for relayer.
     * @param basaltRecipient Basalt address to receive unlocked BST
     * @param amount Amount of wBST to burn
     */
    function withdraw(bytes20 basaltRecipient, uint256 amount) external {
        require(amount > 0, "Zero amount");

        wbst.burn(msg.sender, amount);

        uint256 nonce = withdrawalNonce++;
        emit WithdrawalInitiated(nonce, msg.sender, basaltRecipient, amount);
    }

    /**
     * @notice Add a relayer (governance action).
     */
    function addRelayer(address relayer) external onlyRelayer {
        require(!relayers[relayer], "Already relayer");
        relayers[relayer] = true;
        relayerCount++;
        emit RelayerAdded(relayer);
    }

    /**
     * @notice Remove a relayer (governance action).
     */
    function removeRelayer(address relayer) external onlyRelayer {
        require(relayers[relayer], "Not a relayer");
        require(relayerCount - 1 >= threshold, "Would drop below threshold");
        relayers[relayer] = false;
        relayerCount--;
        emit RelayerRemoved(relayer);
    }

    function recoverSigner(bytes32 ethSignedHash, bytes memory sig) internal pure returns (address) {
        require(sig.length == 65, "Invalid signature length");
        bytes32 r;
        bytes32 s;
        uint8 v;
        assembly {
            r := mload(add(sig, 32))
            s := mload(add(sig, 64))
            v := byte(0, mload(add(sig, 96)))
        }
        return ecrecover(ethSignedHash, v, r, s);
    }
}
