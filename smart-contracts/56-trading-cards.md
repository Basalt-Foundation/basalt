# Trading Card Game

## Category

Gaming / Collectibles

## Summary

A comprehensive trading card game contract built on BST-1155 where each card type has on-chain attributes (attack, defense, mana cost, abilities, element) stored in contract storage. The system supports pack opening via the loot box pattern with weighted rarity drops, deck building with validation rules, on-chain match result recording, card evolution and leveling through experience points, and tournament infrastructure with Escrow-backed prize pools for competitive play.

## Why It's Useful

- **Market need**: Trading card games (TCGs) are one of the most proven game genres, and blockchain-native TCGs solve longstanding problems of centralized card games: players truly own their cards, secondary markets are trustless, and game rules are transparent.
- **User benefit**: Players own their cards as BST-1155 tokens, tradeable on any marketplace. Card attributes and game history are permanently recorded on-chain, preventing disputes and enabling verifiable competition records.
- **Economic model**: Pack sales generate revenue for the game developer. Secondary market trading creates ongoing economic activity. Card evolution mechanics drive engagement and create natural sinks for common cards.
- **Competitive integrity**: Match results recorded on-chain cannot be disputed or retroactively altered. Tournament prize distributions are automatic and trustless via Escrow.
- **Interoperability**: Cards are standard BST-1155 tokens that can be used in other games, displayed in wallets, and traded on the NFT Marketplace.

## Key Features

- **Card definitions**: Each card type has on-chain attributes including name, element, mana cost, attack, defense, health, abilities, rarity, and set/edition membership.
- **Pack system**: Card packs contain a fixed number of cards with guaranteed rarity distributions (e.g., 1 Rare+, 3 Uncommon+, 6 Common). Randomness via BLAKE3 block hash derivation.
- **Deck building**: Players construct decks from their collection with validation rules (minimum/maximum deck size, card copy limits per rarity, element restrictions).
- **Match recording**: Game server submits match results signed with Ed25519. Contract records winner, loser, deck hashes, turn count, and timestamps.
- **Card evolution**: Combine duplicate cards to evolve them into stronger versions. Burn N copies of a card to receive 1 copy of its evolved form with enhanced stats.
- **Experience and leveling**: Cards gain experience points from match participation. Leveled cards have slightly improved stats (within predefined bounds).
- **Tournament system**: Create tournaments with entry fees, bracket structures, and prize pools held in Escrow. Automatic prize distribution based on final standings.
- **Set rotation**: Cards belong to sets with validity periods. Competitive formats can restrict which sets are legal, creating demand cycles.
- **Foil and alternate art**: Special variants of cards with identical gameplay attributes but different metadata (art hash, foil flag), creating collector value.

## Basalt-Specific Advantages

- **BLAKE3 pack randomness**: Pack opening uses BLAKE3-derived randomness from block hashes, providing fast and provably fair card distribution. Players can verify that pack odds match published rates.
- **BST-1155 multi-token efficiency**: A single contract manages all card types with per-ID balances, enabling gas-efficient batch transfers and burns. The Basalt SDK's source-generated dispatch ensures type-safe BST-1155 operations.
- **Escrow tournament prizes**: Tournament prize pools are held in the Escrow system contract (0x...1004), providing trustless, automatic distribution that cannot be withheld by the tournament organizer.
- **Ed25519 match attestation**: Game servers sign match results with Ed25519, native to Basalt and verifiable at minimal gas cost. This enables high-frequency match recording without excessive fees.
- **AOT-compiled game logic**: Deck validation, evolution calculations, and experience point computation execute at native speed, critical for contracts that may process hundreds of match results per block.
- **BNS player identity**: Tournament standings and match history display BNS names, creating a meaningful competitive identity for players.
- **ZK compliance for prize tournaments**: Tournaments with real-money prizes can enforce KYC requirements via the ZK compliance layer, ensuring regulatory compliance for competitive gaming events without exposing player identities publicly.
- **BST-3525 for card slots**: Card evolution stages can be represented as BST-3525 semi-fungible tokens where the slot represents the card type and the value represents the evolution level, enabling nuanced ownership of evolved cards.

## Token Standards Used

- **BST-1155**: Primary standard for all card tokens. Supports fungible quantities per card type (e.g., 4 copies of "Fire Dragon").
- **BST-3525**: Optional representation for evolved card states with slot-based evolution levels.
- **BST-20**: Payment for pack purchases, tournament entry fees, and in-game currency.

## Integration Points

- **Escrow (0x...1004)**: Tournament prize pool management. Entry fees collected and held until tournament completion, then distributed to winners.
- **BNS (0x...1002)**: Player identity for match history and tournament standings.
- **Governance (0x...1003)**: Community governance over card balance changes, set rotation schedules, and tournament rules.
- **NFT Marketplace (contract 51)**: Direct trading of individual cards and complete decks.
- **Loot Crafting (contract 52)**: Shared loot box infrastructure for pack opening mechanics.
- **Achievement System (contract 55)**: Gaming achievements for card collection milestones, tournament wins, and competitive rankings.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum Element : byte
{
    Fire = 0,
    Water = 1,
    Earth = 2,
    Air = 3,
    Light = 4,
    Dark = 5,
    Neutral = 6
}

public enum CardRarity : byte
{
    Common = 0,
    Uncommon = 1,
    Rare = 2,
    SuperRare = 3,
    Legendary = 4,
    Mythic = 5
}

public struct CardDefinition
{
    public ulong CardId;
    public string Name;
    public Element Element;
    public CardRarity Rarity;
    public ulong ManaCost;
    public ulong BaseAttack;
    public ulong BaseDefense;
    public ulong BaseHealth;
    public ulong AbilityId;             // Reference to ability definition
    public ulong SetId;
    public ulong EvolvesFromId;         // 0 if base card
    public ulong EvolvesToId;           // 0 if max evolution
    public ulong EvolutionCost;         // Number of copies to burn for evolution
    public ulong MaxLevel;
    public bool IsFoil;
    public byte[] ArtHash;              // BLAKE3 hash of card art
}

public struct CardInstance
{
    public ulong CardId;
    public ulong Level;
    public ulong ExperiencePoints;
    public ulong MatchesPlayed;
}

public struct PackType
{
    public ulong PackId;
    public string Name;
    public UInt256 Price;
    public ulong CardsPerPack;
    public ulong GuaranteedRareOrAbove; // Number of guaranteed rare+ cards
    public ulong[] CardPool;            // Eligible card IDs
    public ulong[] CardWeights;         // Relative weights for random selection
    public ulong TotalWeight;
    public ulong SetId;
    public bool Active;
}

public struct DeckValidation
{
    public ulong MinCards;
    public ulong MaxCards;
    public ulong MaxCopiesCommon;
    public ulong MaxCopiesUncommon;
    public ulong MaxCopiesRare;
    public ulong MaxCopiesSuperRare;
    public ulong MaxCopiesLegendary;
    public ulong MaxCopiesMythic;
}

public struct MatchResult
{
    public ulong MatchId;
    public Address Player1;
    public Address Player2;
    public Address Winner;
    public byte[] Player1DeckHash;       // BLAKE3 hash of deck composition
    public byte[] Player2DeckHash;
    public ulong TurnCount;
    public ulong Timestamp;
    public ulong TournamentId;           // 0 if casual match
}

public struct Tournament
{
    public ulong TournamentId;
    public string Name;
    public UInt256 EntryFee;
    public UInt256 PrizePool;
    public ulong MaxPlayers;
    public ulong CurrentPlayers;
    public ulong StartTime;
    public ulong EndTime;
    public ulong[] AllowedSets;          // Set rotation for this tournament
    public bool Active;
    public bool Completed;
}

public struct PlayerRecord
{
    public Address Player;
    public ulong TotalWins;
    public ulong TotalLosses;
    public ulong TournamentWins;
    public ulong CurrentRating;          // ELO-like rating
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0205)]
public partial class TradingCardGame : SdkContractBase
{
    // Storage
    private StorageMap<ulong, CardDefinition> _cards;
    private StorageMap<ulong, PackType> _packs;
    private StorageMap<ulong, MatchResult> _matches;
    private StorageMap<ulong, Tournament> _tournaments;
    private StorageValue<ulong> _nextCardId;
    private StorageValue<ulong> _nextPackId;
    private StorageValue<ulong> _nextMatchId;
    private StorageValue<ulong> _nextTournamentId;
    private StorageValue<Address> _gameAdmin;
    private StorageValue<Address> _gameServer;
    private StorageValue<ulong> _openNonce;
    private StorageValue<DeckValidation> _deckRules;

    // Composite key storage:
    // CardInstance keyed by BLAKE3(ownerAddress || cardId)
    // PlayerRecord keyed by player address
    // Tournament entries keyed by BLAKE3(tournamentId || playerAddress)

    // --- Card Management (Admin) ---

    /// <summary>
    /// Define a new card type with full attributes.
    /// Only callable by game admin.
    /// </summary>
    public ulong DefineCard(
        string name,
        Element element,
        CardRarity rarity,
        ulong manaCost,
        ulong baseAttack,
        ulong baseDefense,
        ulong baseHealth,
        ulong abilityId,
        ulong setId,
        ulong evolvesFromId,
        ulong evolvesToId,
        ulong evolutionCost,
        ulong maxLevel,
        byte[] artHash
    );

    /// <summary>
    /// Create a foil variant of an existing card.
    /// Same gameplay attributes, different art hash and foil flag.
    /// </summary>
    public ulong CreateFoilVariant(ulong baseCardId, byte[] foilArtHash);

    /// <summary>
    /// Define a card pack with its drop table.
    /// </summary>
    public ulong DefinePack(
        string name,
        UInt256 price,
        ulong cardsPerPack,
        ulong guaranteedRareOrAbove,
        ulong[] cardPool,
        ulong[] cardWeights,
        ulong setId
    );

    /// <summary>
    /// Set deck validation rules for competitive play.
    /// </summary>
    public void SetDeckRules(DeckValidation rules);

    // --- Pack Opening ---

    /// <summary>
    /// Purchase and open a card pack. Burns BST payment and mints
    /// random cards based on the pack's drop table.
    /// Returns the card IDs received.
    ///
    /// Guaranteed rarity: First N cards are drawn from rare+ pool.
    /// Remaining cards are drawn from the full pool.
    /// Randomness: BLAKE3(blockHash || txHash || callerAddress || nonce)
    /// </summary>
    public ulong[] OpenPack(ulong packId);

    // --- Card Evolution and Leveling ---

    /// <summary>
    /// Evolve a card by burning the required number of copies.
    /// Mints one copy of the evolved card with reset level/experience.
    /// </summary>
    public void EvolveCard(ulong cardId);

    /// <summary>
    /// Add experience points to a card instance after a match.
    /// If experience exceeds the level threshold, the card levels up.
    /// Only callable by game server.
    /// </summary>
    public void AddExperience(Address owner, ulong cardId, ulong xpAmount);

    /// <summary>
    /// Get the effective stats of a card instance including level bonuses.
    /// Attack/Defense/Health increase by 2% per level.
    /// </summary>
    public (ulong Attack, ulong Defense, ulong Health) GetEffectiveStats(
        Address owner,
        ulong cardId
    );

    // --- Deck Management ---

    /// <summary>
    /// Register a deck composition on-chain. The deck hash is recorded
    /// for match verification. Returns true if the deck passes validation.
    /// </summary>
    public bool RegisterDeck(ulong[] cardIds, ulong[] quantities);

    /// <summary>
    /// Validate a deck against the current competitive rules.
    /// Checks deck size, copy limits per rarity, and set legality.
    /// </summary>
    public bool ValidateDeck(ulong[] cardIds, ulong[] quantities, ulong[] allowedSets);

    // --- Match Recording ---

    /// <summary>
    /// Record a match result. Only callable by the authorized game server.
    /// Verifies server Ed25519 signature over the match data.
    /// Updates player records and ELO ratings.
    /// </summary>
    public ulong RecordMatch(
        Address player1,
        Address player2,
        Address winner,
        byte[] player1DeckHash,
        byte[] player2DeckHash,
        ulong turnCount,
        byte[] serverSignature
    );

    // --- Tournaments ---

    /// <summary>
    /// Create a tournament. Admin deposits initial prize pool as tx value.
    /// Entry fees are added to the prize pool via Escrow.
    /// </summary>
    public ulong CreateTournament(
        string name,
        UInt256 entryFee,
        ulong maxPlayers,
        ulong startTime,
        ulong endTime,
        ulong[] allowedSets
    );

    /// <summary>
    /// Enter a tournament. Caller pays entry fee which is added
    /// to the Escrow-held prize pool.
    /// </summary>
    public void EnterTournament(ulong tournamentId);

    /// <summary>
    /// Record a tournament match result. Updates bracket progression.
    /// Only callable by game server.
    /// </summary>
    public void RecordTournamentMatch(
        ulong tournamentId,
        ulong matchId,
        ulong round
    );

    /// <summary>
    /// Finalize a tournament and distribute prizes from Escrow.
    /// Prize distribution: 1st = 50%, 2nd = 25%, 3rd/4th = 12.5% each.
    /// </summary>
    public void FinalizeTournament(
        ulong tournamentId,
        Address[] finalStandings
    );

    // --- View Functions ---

    public CardDefinition GetCard(ulong cardId);
    public CardInstance GetCardInstance(Address owner, ulong cardId);
    public PackType GetPack(ulong packId);
    public MatchResult GetMatch(ulong matchId);
    public Tournament GetTournament(ulong tournamentId);
    public PlayerRecord GetPlayerRecord(Address player);
    public ulong GetCardBalance(Address owner, ulong cardId);
    public DeckValidation GetDeckRules();

    // --- Internal ---

    /// <summary>
    /// Calculate ELO rating change based on match outcome.
    /// K-factor = 32 for standard matches, 64 for tournament matches.
    /// </summary>
    private (ulong NewRatingWinner, ulong NewRatingLoser) CalculateEloChange(
        ulong winnerRating,
        ulong loserRating,
        ulong kFactor
    );

    /// <summary>
    /// Verify game server signature over match data.
    /// </summary>
    private bool VerifyMatchSignature(
        Address player1,
        Address player2,
        Address winner,
        byte[] player1DeckHash,
        byte[] player2DeckHash,
        ulong turnCount,
        byte[] signature
    );
}
```

## Complexity

**High** -- The contract encompasses multiple interacting subsystems: card definition and pack distribution (loot box pattern), deck building with multi-dimensional validation (size, rarity copy limits, set legality), match recording with ELO rating calculations, card evolution requiring multi-token burns, experience-based leveling with stat scaling, and tournament management with bracket tracking and Escrow prize distribution. Each subsystem has its own state management and validation rules.

## Priority

**P2** -- Trading card games are a proven genre with strong community appeal but require a mature gaming ecosystem to thrive. The contract should be prioritized once the core gaming infrastructure (Loot Crafting contract 52, Play-to-Earn contract 53) is established and game developers are actively building on Basalt.
