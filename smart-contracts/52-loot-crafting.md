# Loot Box / Crafting System

## Category

Gaming Infrastructure / On-Chain Economy

## Summary

A game-item management contract built on BST-1155 multi-tokens that supports deterministic crafting recipes and randomized loot box drops. Crafting consumes (burns) input items and mints output items according to predefined recipes, while loot boxes use BLAKE3 hashing of block data combined with user entropy to produce weighted random drops across configurable rarity tiers. The system provides the foundational economic layer for on-chain gaming ecosystems on Basalt.

## Why It's Useful

- **Market need**: On-chain gaming requires verifiable item creation, destruction, and randomization that players can trust. Off-chain loot box systems are opaque and frequently criticized for hidden odds.
- **User benefit**: Players can verify that loot box odds match published drop rates by inspecting the contract logic. Crafting recipes are transparent and immutable, preventing developers from silently changing requirements.
- **Economic value**: Burn-to-craft mechanics create deflationary pressure on common items, supporting healthy in-game economies. Loot boxes provide a monetization mechanism for game developers that is provably fair.
- **Composability**: Items minted by this contract are standard BST-1155 tokens tradeable on the NFT Marketplace (contract 51) or usable in other game contracts (trading cards, play-to-earn).
- **Developer tooling**: Game developers deploy recipe configurations rather than writing custom smart contract logic, lowering the barrier to building on-chain game economies.

## Key Features

- **BST-1155 item management**: All game items are BST-1155 tokens with per-token metadata (name, description, rarity, attributes) stored in contract storage.
- **Crafting recipes**: Admin-defined recipes specify input items (token IDs and quantities to burn) and output items (token IDs and quantities to mint). Recipes can require a crafting fee in BST.
- **Rarity tiers**: Five tiers (Common, Uncommon, Rare, Epic, Legendary) with configurable drop weights per loot box type.
- **Loot box system**: Purchasing a loot box burns BST payment and generates random drops. Randomness derived from `BLAKE3(blockHash || txHash || userAddress || nonce)` ensures deterministic yet unpredictable outcomes.
- **Weighted random selection**: Drop tables define items and their weights per rarity tier. The random value maps to cumulative weight ranges for provably fair selection.
- **Item metadata on-chain**: Each item type has attributes (attack, defense, durability, level requirement) stored in contract storage, queryable by any contract or front-end.
- **Supply caps**: Optional maximum supply per item type. Loot boxes and recipes respect caps and fail gracefully when limits are reached.
- **Batch crafting**: Craft multiple copies of a recipe in a single transaction, burning proportional inputs and minting proportional outputs.
- **Recipe versioning**: Recipes can be deprecated (no new crafts) without destroying existing items. New recipes can reference the same output items.
- **Admin controls**: Game developer can add/deprecate recipes, modify loot box contents, adjust drop rates, and pause the system. Optionally transferable to governance.

## Basalt-Specific Advantages

- **BLAKE3 randomness**: Basalt's native BLAKE3 hash function provides fast, collision-resistant randomness derivation. The combination of block hash, transaction hash, and user address creates entropy that is deterministic for verification but unpredictable before block finalization.
- **AOT-compiled performance**: Complex crafting validation (checking multiple input items, verifying quantities, computing random drops across weighted tables) executes at near-native speed thanks to AOT compilation. This is critical for batch crafting operations that touch many storage slots.
- **BST-1155 native support**: Basalt's SDK contract system has first-class BST-1155 support with source-generated dispatch, meaning multi-token operations (batch mint, batch burn) are type-safe and gas-efficient.
- **Escrow integration**: Loot box purchases can route payment through the Escrow system contract, enabling delayed-reveal mechanics where the loot box is purchased in one transaction and opened in another (after block finalization ensures randomness commitment).
- **ZK compliance for regulated items**: If game items represent real-value assets (e.g., tournament entry tokens), the crafting system can enforce compliance checks via SchemaRegistry and IssuerRegistry, ensuring only verified players participate.
- **Flat state DB performance**: Item balances and recipe lookups benefit from Basalt's FlatStateDb O(1) dictionary caches, avoiding Merkle trie traversals for the frequent balance checks that crafting requires.

## Token Standards Used

- **BST-1155**: Primary standard for all game items. Supports fungible quantities per token ID (e.g., 50 iron ore, 3 legendary swords).
- **BST-20**: Payment token for loot box purchases and crafting fees.

## Integration Points

- **Escrow (0x...1004)**: Holds payment for delayed-reveal loot boxes. Funds released to game developer treasury upon successful opening.
- **Governance (0x...1003)**: Optional governance control over recipe parameters and drop rates for community-governed games.
- **BNS (0x...1002)**: Player profiles referenced by BNS names in leaderboards and crafting history.
- **NFT Marketplace (contract 51)**: Items minted by this contract are directly tradeable on the marketplace.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum Rarity : byte
{
    Common = 0,
    Uncommon = 1,
    Rare = 2,
    Epic = 3,
    Legendary = 4
}

public struct ItemType
{
    public ulong ItemId;
    public string Name;
    public Rarity Rarity;
    public ulong MaxSupply;          // 0 = unlimited
    public ulong CurrentSupply;
    public ulong Attack;
    public ulong Defense;
    public ulong Durability;
    public ulong LevelRequirement;
}

public struct RecipeInput
{
    public ulong ItemId;
    public ulong Quantity;           // Amount to burn
}

public struct RecipeOutput
{
    public ulong ItemId;
    public ulong Quantity;           // Amount to mint
}

public struct Recipe
{
    public ulong RecipeId;
    public ulong[] InputItemIds;
    public ulong[] InputQuantities;
    public ulong OutputItemId;
    public ulong OutputQuantity;
    public UInt256 CraftingFee;      // BST cost
    public bool Active;
}

public struct DropTableEntry
{
    public ulong ItemId;
    public ulong Weight;             // Relative weight for random selection
}

public struct LootBoxType
{
    public ulong LootBoxId;
    public string Name;
    public UInt256 Price;
    public ulong ItemsPerBox;        // Number of items rolled per opening
    public ulong[] DropItemIds;
    public ulong[] DropWeights;
    public ulong TotalWeight;        // Sum of all weights (precomputed)
    public bool Active;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0201)]
public partial class LootCrafting : SdkContractBase
{
    // Storage
    private StorageMap<ulong, ItemType> _itemTypes;
    private StorageMap<ulong, Recipe> _recipes;
    private StorageMap<ulong, LootBoxType> _lootBoxTypes;
    private StorageValue<ulong> _nextItemId;
    private StorageValue<ulong> _nextRecipeId;
    private StorageValue<ulong> _nextLootBoxId;
    private StorageValue<Address> _gameAdmin;
    private StorageValue<Address> _treasury;
    private StorageValue<ulong> _openNonce;       // Monotonic nonce for randomness
    private StorageValue<bool> _paused;

    // --- Item Management (Admin) ---

    /// <summary>
    /// Register a new item type with metadata and optional supply cap.
    /// Only callable by game admin.
    /// </summary>
    public ulong RegisterItemType(
        string name,
        Rarity rarity,
        ulong maxSupply,
        ulong attack,
        ulong defense,
        ulong durability,
        ulong levelRequirement
    );

    /// <summary>
    /// Update metadata for an existing item type.
    /// Does not affect existing minted items' on-chain attributes.
    /// </summary>
    public void UpdateItemType(
        ulong itemId,
        string name,
        ulong attack,
        ulong defense,
        ulong durability,
        ulong levelRequirement
    );

    // --- Recipe Management (Admin) ---

    /// <summary>
    /// Add a crafting recipe. Input items are burned, output items are minted.
    /// </summary>
    public ulong AddRecipe(
        ulong[] inputItemIds,
        ulong[] inputQuantities,
        ulong outputItemId,
        ulong outputQuantity,
        UInt256 craftingFee
    );

    /// <summary>
    /// Deprecate a recipe. Existing items are unaffected.
    /// </summary>
    public void DeprecateRecipe(ulong recipeId);

    // --- Loot Box Management (Admin) ---

    /// <summary>
    /// Create a loot box type with weighted drop table.
    /// </summary>
    public ulong CreateLootBoxType(
        string name,
        UInt256 price,
        ulong itemsPerBox,
        ulong[] dropItemIds,
        ulong[] dropWeights
    );

    /// <summary>
    /// Update drop rates for an existing loot box type.
    /// </summary>
    public void UpdateDropTable(
        ulong lootBoxId,
        ulong[] dropItemIds,
        ulong[] dropWeights
    );

    /// <summary>
    /// Deactivate a loot box type.
    /// </summary>
    public void DeactivateLootBox(ulong lootBoxId);

    // --- Player Actions ---

    /// <summary>
    /// Craft an item by burning input items and paying the crafting fee.
    /// Verifies the caller holds sufficient quantities of all inputs.
    /// Burns inputs atomically, then mints the output.
    /// </summary>
    public void Craft(ulong recipeId, ulong multiplier);

    /// <summary>
    /// Open a loot box by paying the price. Burns payment and mints
    /// random items based on the weighted drop table.
    /// Returns the list of item IDs received.
    ///
    /// Randomness: BLAKE3(blockHash || txHash || callerAddress || nonce)
    /// Each item in the box uses an incremented nonce for independent rolls.
    /// </summary>
    public ulong[] OpenLootBox(ulong lootBoxId);

    // --- View Functions ---

    public ItemType GetItemType(ulong itemId);
    public Recipe GetRecipe(ulong recipeId);
    public LootBoxType GetLootBoxType(ulong lootBoxId);
    public ulong GetItemBalance(Address owner, ulong itemId);
    public ulong GetItemSupply(ulong itemId);

    /// <summary>
    /// Verify a past loot box opening by replaying the BLAKE3 random
    /// selection with the stored parameters. Returns true if the
    /// claimed drops match the deterministic outcome.
    /// </summary>
    public bool VerifyDrop(
        byte[] blockHash,
        byte[] txHash,
        Address opener,
        ulong nonce,
        ulong lootBoxId,
        ulong[] claimedItemIds
    );

    // --- Internal Helpers ---

    /// <summary>
    /// Generate a pseudorandom uint64 from block hash, tx hash,
    /// caller address, and nonce using BLAKE3.
    /// </summary>
    private ulong GenerateRandom(ulong nonce);

    /// <summary>
    /// Select an item from a drop table using a random value and
    /// cumulative weight mapping.
    /// </summary>
    private ulong SelectFromDropTable(LootBoxType lootBox, ulong randomValue);

    /// <summary>
    /// Burn BST-1155 tokens from the caller's balance.
    /// </summary>
    private void BurnItems(Address owner, ulong itemId, ulong quantity);

    /// <summary>
    /// Mint BST-1155 tokens to the caller's balance.
    /// Checks supply cap before minting.
    /// </summary>
    private void MintItems(Address recipient, ulong itemId, ulong quantity);
}
```

## Complexity

**High** -- The contract combines two distinct subsystems (crafting and loot boxes), each with their own validation logic. Crafting requires atomic multi-token burn-and-mint with supply cap enforcement. Loot box randomness must be provably fair and verifiable, requiring careful entropy derivation from block data. Weighted random selection across variable-length drop tables adds algorithmic complexity. Batch crafting multipliers introduce overflow risks that must be guarded against.

## Priority

**P1** -- While not as foundational as the marketplace itself, a crafting and loot box system is essential for the gaming vertical. It provides the economic primitives (item creation, destruction, randomized distribution) that game developers need to build on-chain economies. It becomes a P0 dependency once any gaming dApp launches on Basalt.
