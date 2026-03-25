using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace UndoMod;

public class StateSnapshot
{
    // ── FAILED Sentinel ──
    public bool IsFailed { get; private init; }
    private static readonly StateSnapshot FailedSentinel = new() { IsFailed = true };

    // ── Data Structures ──

    private record struct CreatureData(
        uint CombatId,
        Creature CreatureRef,
        int CurrentHp,
        int MaxHp,
        int Block,
        List<PowerData> Powers,
        Vector2? VisualGlobalPosition,
        Vector2? VisualBodyScale);

    private record struct PowerData(
        ModelId Id,
        int Amount,
        int AmountOnTurnStart,
        bool SkipNextDurationTick,
        object? InternalData,
        object? FacingDirection);

    private record struct MonsterMoveSnapshot(
        string? NextMoveStateId,
        string? CurrentStateId,
        bool PerformedFirstMove,
        bool SpawnedThisTurn,
        List<string> StateLogIds,
        Dictionary<string, bool> MovePerformedAtLeastOnce);

    private record struct RelicData(
        ModelId Id,
        int StackCount,
        bool IsWax,
        bool IsMelted,
        object Status,
        object? DynamicVarsClone);

    // ── Snapshot Data ──

    private readonly List<CreatureData> _creatureStates = new();
    private readonly Dictionary<PileType, List<CardModel>> _savedPiles = new();
    private readonly Dictionary<CardModel, CardModel> _cardClones = new();

    private int _energy;
    private int _stars;
    private int _roundNumber;
    private CombatSide _currentSide;

    public int RoundNumber => _roundNumber;

    // RNG
    private readonly Dictionary<RunRngType, (uint seed, int counter)> _runRngStates = new();
    private readonly Dictionary<uint, (uint seed, int counter)> _monsterRngStates = new();

    // Monster moves
    private readonly Dictionary<uint, MonsterMoveSnapshot> _monsterMoveStates = new();

    // Orbs
    private readonly List<OrbModel> _savedOrbRefs = new();
    private readonly Dictionary<OrbModel, OrbModel> _orbClones = new();
    private int _orbCapacity;
    private bool _hasOrbData;

    // Relics
    private readonly List<RelicData> _relicStates = new();
    private readonly Dictionary<RelicModel, object> _relicClones = new();

    // Pets
    private readonly List<uint> _petCombatIds = new();

    // Potions
    private readonly List<PotionModel?> _potionSlotRefs = new();
    private readonly Dictionary<PotionModel, PotionModel> _potionClones = new();

    // Creature roster (to detect summons for removal on undo)
    private readonly HashSet<uint> _creatureCombatIds = new();

    // Combat history
    private List<object>? _savedHistoryEntries;

    // Escaped creatures
    private readonly List<Creature> _escapedCreatures = new();

    // Gold
    private int _gold;

    // Run deck state (for cross-turn undo with card-stealing enemies)
    // Captures Player.Deck card list + HasBeenRemovedFromState flags
    private readonly List<CardModel> _savedDeckCards = new();
    private readonly Dictionary<CardModel, bool> _cardRemovedFlags = new();

    // ── Reflection Caches ──

    private static readonly FieldInfo CreatureHpField =
        AccessTools.Field(typeof(Creature), "_currentHp");
    private static readonly FieldInfo CreatureMaxHpField =
        AccessTools.Field(typeof(Creature), "_maxHp");
    private static readonly FieldInfo CreatureBlockField =
        AccessTools.Field(typeof(Creature), "_block");
    private static readonly FieldInfo CreaturePowersField =
        AccessTools.Field(typeof(Creature), "_powers");

    private static readonly FieldInfo PcsEnergyField =
        AccessTools.Field(typeof(PlayerCombatState), "_energy");
    private static readonly FieldInfo PcsStarsField =
        AccessTools.Field(typeof(PlayerCombatState), "_stars");
    private static readonly FieldInfo? PcsPetsField =
        AccessTools.Field(typeof(PlayerCombatState), "_pets");

    private static readonly FieldInfo CardPileCardsField =
        AccessTools.Field(typeof(CardPile), "_cards");

    private static readonly MethodInfo? StateTrackerSubscribeCardMethod =
        AccessTools.Method(typeof(CombatStateTracker), "Subscribe", new[] { typeof(CardModel) });
    private static readonly MethodInfo? StateTrackerUnsubscribeCardMethod =
        AccessTools.Method(typeof(CombatStateTracker), "Unsubscribe", new[] { typeof(CardModel) });

    private static readonly FieldInfo PowerAmountField =
        AccessTools.Field(typeof(PowerModel), "_amount");
    private static readonly FieldInfo PowerAmountOnTurnStartField =
        AccessTools.Field(typeof(PowerModel), "_amountOnTurnStart");
    private static readonly FieldInfo PowerSkipField =
        AccessTools.Field(typeof(PowerModel), "_skipNextDurationTick");
    private static readonly FieldInfo? PowerInternalDataField =
        AccessTools.Field(typeof(PowerModel), "_internalData");
    private static readonly MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo? EnergyCostCardField =
        AccessTools.Field(typeof(CardEnergyCost), "_card");

    private static readonly Type? SurroundedPowerType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Powers.SurroundedPower");
    private static readonly FieldInfo? SurroundedFacingField =
        SurroundedPowerType != null ? AccessTools.Field(SurroundedPowerType, "_facing") : null;

    private static readonly FieldInfo MonsterRngField =
        AccessTools.Field(typeof(MonsterModel), "_rng");
    private static readonly FieldInfo? MonsterSpawnedField =
        AccessTools.Field(typeof(MonsterModel), "_spawnedThisTurn");
    private static readonly FieldInfo? MonsterMoveStateMachineField =
        AccessTools.Field(typeof(MonsterModel), "_moveStateMachine");
    private static readonly PropertyInfo? NextMoveProp =
        AccessTools.Property(typeof(MonsterModel), "NextMove");

    private static readonly Type? SmType =
        AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine");
    private static readonly Type? MoveStateType =
        AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState");
    private static readonly FieldInfo? SmCurrentStateField =
        SmType != null ? AccessTools.Field(SmType, "_currentState") : null;
    private static readonly FieldInfo? SmPerformedFirstMoveField =
        SmType != null ? AccessTools.Field(SmType, "_performedFirstMove") : null;
    private static readonly FieldInfo? MoveStatePerformedField =
        MoveStateType != null ? AccessTools.Field(MoveStateType, "_performedAtLeastOnce") : null;

    private static readonly PropertyInfo? MonsterStateIdProperty;
    private static readonly MethodInfo? ForceCurrentStateMethod;

    private static readonly FieldInfo? OrbQueueOrbsField =
        AccessTools.Field(typeof(OrbQueue), "_orbs");
    private static readonly FieldInfo? OrbQueueCapacityField =
        AccessTools.Field(typeof(OrbQueue), "<Capacity>k__BackingField");

    private static readonly FieldInfo? RelicDynamicVarsField =
        AccessTools.Field(typeof(RelicModel), "_dynamicVars");
    private static readonly PropertyInfo? RelicStatusProperty =
        AccessTools.Property(typeof(RelicModel), "Status");
    private static readonly FieldInfo? RelicStackCountField =
        AccessTools.Field(typeof(RelicModel), "<StackCount>k__BackingField");
    private static readonly MethodInfo? InvokeDisplayAmountChangedMethod =
        AccessTools.Method(typeof(RelicModel), "InvokeDisplayAmountChanged");

    private static readonly FieldInfo PlayerPotionSlotsField =
        AccessTools.Field(typeof(Player), "_potionSlots");
    private static readonly FieldInfo? PotionOwnerField =
        AccessTools.Field(typeof(PotionModel), "_owner");

    private static readonly PropertyInfo? CmHistoryProperty =
        AccessTools.Property(typeof(CombatManager), "History");
    private static readonly FieldInfo? HistoryEntriesField =
        CmHistoryProperty?.PropertyType != null
            ? AccessTools.Field(CmHistoryProperty.PropertyType, "_entries")
            : null;

    private static readonly PropertyInfo? EscapedCreaturesProp =
        AccessTools.Property(typeof(CombatState), "EscapedCreatures");

    private static readonly FieldInfo PlayerGoldField =
        AccessTools.Field(typeof(Player), "_gold");

    // Run deck — Player.Deck is a CardPile, uses same CardPileCardsField
    private static readonly FieldInfo? CardRemovedFromStateField =
        AccessTools.Field(typeof(CardModel), "<HasBeenRemovedFromState>k__BackingField");

    // RunState._allCards — the global card registry
    private static readonly FieldInfo? RunStateAllCardsField =
        AccessTools.Field(typeof(RunState), "_allCards");

    private static readonly FieldInfo RunRngDictField =
        AccessTools.Field(typeof(RunRngSet), "_rngs");
    private static readonly PropertyInfo RunManagerStateProperty =
        AccessTools.Property(typeof(RunManager), "State");
    private static readonly FieldInfo CombatManagerStateField =
        AccessTools.Field(typeof(CombatManager), "_state");

    private static readonly FieldInfo CsAlliesField =
        AccessTools.Field(typeof(CombatState), "_allies");
    private static readonly FieldInfo CsEnemiesField =
        AccessTools.Field(typeof(CombatState), "_enemies");
    private static readonly FieldInfo? CsCreaturesChangedField =
        AccessTools.Field(typeof(CombatState), "CreaturesChanged");

    private static readonly FieldInfo[] CardMutableFields = InitCardMutableFields();

    private static readonly HashSet<string> OrbFieldSkipSet = new()
    {
        "_canonicalInstance", "_owner",
        "<Id>k__BackingField", "<IsMutable>k__BackingField",
        "<CategorySortingId>k__BackingField", "<EntrySortingId>k__BackingField",
        "_dynamicVars"
    };

    private static readonly HashSet<string> PotionFieldSkipSet = new()
    {
        "_canonicalInstance", "_owner",
        "<Id>k__BackingField", "<IsMutable>k__BackingField",
        "<CategorySortingId>k__BackingField", "<EntrySortingId>k__BackingField",
        "_dynamicVars"
    };

    static StateSnapshot()
    {
        var monsterStateType = AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterState");
        MonsterStateIdProperty = monsterStateType != null
            ? AccessTools.Property(monsterStateType, "Id") : null;
        ForceCurrentStateMethod = SmType != null
            ? AccessTools.Method(SmType, "ForceCurrentState") : null;
    }

    private static FieldInfo[] InitCardMutableFields()
    {
        var skipSet = new HashSet<string>
        {
            "_cloneOf", "_canonicalInstance", "_deckVersion", "_owner",
            "_isDupe", "_currentTarget", "_isEnchantmentPreview",
            "<Id>k__BackingField", "<IsMutable>k__BackingField",
            "<CategorySortingId>k__BackingField", "<EntrySortingId>k__BackingField"
        };

        var fields = new List<FieldInfo>();
        var type = typeof(CardModel);
        while (type != null && type != typeof(object))
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                BindingFlags.DeclaredOnly))
            {
                if (!field.IsLiteral && !field.IsInitOnly && !skipSet.Contains(field.Name))
                    fields.Add(field);
            }
            type = type.BaseType;
        }
        return fields.ToArray();
    }

    // ── Capture ──

    public static StateSnapshot? Capture()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return null;

        var cs = CombatManagerStateField.GetValue(cm) as CombatState;
        if (cs == null) return null;

        try
        {
            var snapshot = new StateSnapshot
            {
                _roundNumber = cs.RoundNumber,
                _currentSide = cs.CurrentSide
            };

            // Creature roster
            foreach (var creature in cs.Creatures)
            {
                if (creature.CombatId != null)
                    snapshot._creatureCombatIds.Add(creature.CombatId.Value);
            }

            // Creature states
            foreach (var creature in cs.Creatures)
            {
                if (creature.CombatId == null) continue;
                var combatId = creature.CombatId.Value;

                var powers = new List<PowerData>();
                foreach (var power in creature.Powers)
                {
                    object? internalDataClone = null;
                    var internalData = PowerInternalDataField?.GetValue(power);
                    if (internalData != null)
                        internalDataClone = MemberwiseCloneMethod.Invoke(internalData, null);

                    object? facingDir = null;
                    if (SurroundedFacingField != null && SurroundedPowerType != null
                        && SurroundedPowerType.IsInstanceOfType(power))
                        facingDir = SurroundedFacingField.GetValue(power);

                    powers.Add(new PowerData(
                        power.Id,
                        (int)PowerAmountField.GetValue(power)!,
                        (int)PowerAmountOnTurnStartField.GetValue(power)!,
                        (bool)PowerSkipField.GetValue(power)!,
                        internalDataClone,
                        facingDir));
                }

                Vector2? visualPos = null;
                Vector2? bodyScale = null;
                var nCreatureNode = NCombatRoom.Instance?.GetCreatureNode(creature);
                if (nCreatureNode != null)
                {
                    visualPos = nCreatureNode.GlobalPosition;
                    bodyScale = nCreatureNode.Body?.Scale;
                }

                snapshot._creatureStates.Add(new CreatureData(
                    combatId, creature, creature.CurrentHp, creature.MaxHp,
                    creature.Block, powers, visualPos, bodyScale));

                if (creature.Monster != null)
                {
                    var rng = MonsterRngField.GetValue(creature.Monster) as Rng;
                    if (rng != null)
                        snapshot._monsterRngStates[combatId] = (rng.Seed, rng.Counter);
                    CaptureMonsterMoves(snapshot, creature.Monster, combatId);
                }
            }

            // Player combat state
            foreach (var ally in cs.Allies)
            {
                var player = ally.Player;
                if (player == null) continue;

                var pcs = player.PlayerCombatState;
                snapshot._energy = (int)PcsEnergyField.GetValue(pcs)!;
                snapshot._stars = (int)PcsStarsField.GetValue(pcs)!;

                foreach (var pile in pcs.AllPiles)
                    snapshot._savedPiles[pile.Type] = pile.Cards.ToList();

                foreach (var card in pcs.AllCards)
                    snapshot._cardClones[card] = (CardModel)card.MutableClone();

                CaptureOrbs(snapshot, pcs);
                CapturePets(snapshot, pcs);
                CaptureRelics(snapshot, player);
                CapturePotions(snapshot, player);
                CaptureRunDeck(snapshot, player, pcs);
                snapshot._gold = (int)PlayerGoldField.GetValue(player)!;
            }

            snapshot._escapedCreatures.AddRange(cs.EscapedCreatures);
            CaptureRunRng(snapshot);
            CaptureCombatHistory(snapshot);

            ModEntry.Log($"Capture: round={snapshot._roundNumber} " +
                $"creatures={snapshot._creatureStates.Count} energy={snapshot._energy} " +
                $"cards={snapshot._cardClones.Count} gold={snapshot._gold}");
            return snapshot;
        }
        catch (Exception ex)
        {
            ModEntry.Log($"Capture FAILED: {ex}");
            return FailedSentinel;
        }
    }

    // Overload for backward compatibility with UndoManager
    public static StateSnapshot? Capture(CombatState _) => Capture();

    private static void CaptureMonsterMoves(
        StateSnapshot snapshot, MonsterModel monster, uint combatId)
    {
        var sm = monster.MoveStateMachine;
        if (sm == null) return;

        string? nextMoveId = null;
        var nextMove = monster.NextMove;
        if (nextMove != null)
            nextMoveId = MonsterStateIdProperty?.GetValue(nextMove) as string;

        string? currentStateId = null;
        var currentState = SmCurrentStateField?.GetValue(sm);
        if (currentState != null)
            currentStateId = MonsterStateIdProperty?.GetValue(currentState) as string;

        bool performedFirstMove = SmPerformedFirstMoveField != null &&
            (bool)SmPerformedFirstMoveField.GetValue(sm)!;
        bool spawnedThisTurn = MonsterSpawnedField != null &&
            (bool)MonsterSpawnedField.GetValue(monster)!;

        var stateLogIds = new List<string>();
        var stateLogProp = SmType != null ? AccessTools.Property(SmType, "StateLog") : null;
        if (stateLogProp?.GetValue(sm) is System.Collections.IList stateLog)
        {
            foreach (var state in stateLog)
            {
                var id = MonsterStateIdProperty?.GetValue(state) as string;
                if (id != null) stateLogIds.Add(id);
            }
        }

        var movePerformed = new Dictionary<string, bool>();
        var statesProp = SmType != null ? AccessTools.Property(SmType, "States") : null;
        if (statesProp?.GetValue(sm) is System.Collections.IDictionary statesDict &&
            MoveStatePerformedField != null)
        {
            foreach (System.Collections.DictionaryEntry entry in statesDict)
            {
                var key = entry.Key as string;
                if (key != null && MoveStateType != null &&
                    MoveStateType.IsInstanceOfType(entry.Value))
                {
                    movePerformed[key] = (bool)MoveStatePerformedField.GetValue(entry.Value)!;
                }
            }
        }

        snapshot._monsterMoveStates[combatId] = new MonsterMoveSnapshot(
            nextMoveId, currentStateId, performedFirstMove, spawnedThisTurn,
            stateLogIds, movePerformed);
    }

    private static void CaptureOrbs(StateSnapshot snapshot, PlayerCombatState pcs)
    {
        var orbQueue = pcs.OrbQueue;
        if (orbQueue == null) return;

        snapshot._hasOrbData = true;
        snapshot._orbCapacity = orbQueue.Capacity;

        foreach (var orb in orbQueue.Orbs)
        {
            snapshot._savedOrbRefs.Add(orb);
            snapshot._orbClones[orb] = (OrbModel)orb.MutableClone();
        }
    }

    private static void CapturePets(StateSnapshot snapshot, PlayerCombatState pcs)
    {
        if (PcsPetsField == null) return;
        if (PcsPetsField.GetValue(pcs) is not System.Collections.IList petsList) return;

        foreach (var pet in petsList)
        {
            if (pet is Creature creature && creature.CombatId != null)
                snapshot._petCombatIds.Add(creature.CombatId.Value);
        }
    }

    private static void CaptureRelics(StateSnapshot snapshot, Player player)
    {
        foreach (var relic in player.Relics)
        {
            object? dvClone = null;
            try { dvClone = relic.DynamicVars?.Clone(relic); }
            catch { }

            var status = RelicStatusProperty?.GetValue(relic);
            snapshot._relicStates.Add(new RelicData(
                relic.Id, relic.StackCount, relic.IsWax, relic.IsMelted, status!, dvClone));

            try
            {
                var clone = MemberwiseCloneMethod.Invoke(relic, null);
                if (clone != null)
                    snapshot._relicClones[relic] = clone;
            }
            catch { }
        }
    }

    private static void CapturePotions(StateSnapshot snapshot, Player player)
    {
        if (PlayerPotionSlotsField == null) return;
        foreach (var potion in player.PotionSlots)
        {
            snapshot._potionSlotRefs.Add(potion);
            if (potion != null && !snapshot._potionClones.ContainsKey(potion))
                snapshot._potionClones[potion] = (PotionModel)potion.MutableClone();
        }
    }

    /// <summary>
    /// Player.Deck (런 덱) 상태를 캡처한다.
    /// 크로스턴 undo 후 적이 카드를 다시 훔칠 때 RemoveFromDeck이
    /// 정상 작동하도록 하기 위함.
    /// 또한 전투 카드의 HasBeenRemovedFromState 플래그도 캡처.
    /// </summary>
    private static void CaptureRunDeck(StateSnapshot snapshot, Player player, PlayerCombatState pcs)
    {
        // 1. Player.Deck 카드 목록 (참조 저장)
        snapshot._savedDeckCards.AddRange(player.Deck.Cards);

        // 2. 전투 카드의 DeckVersion에 대한 HasBeenRemovedFromState 플래그
        foreach (var card in pcs.AllCards)
        {
            var deckVersion = card.DeckVersion;
            if (deckVersion != null && !snapshot._cardRemovedFlags.ContainsKey(deckVersion))
                snapshot._cardRemovedFlags[deckVersion] = deckVersion.HasBeenRemovedFromState;
        }

        ModEntry.Log($"CaptureRunDeck: deck={snapshot._savedDeckCards.Count} cards, " +
            $"removedFlags={snapshot._cardRemovedFlags.Count}");
    }

    private static void CaptureCombatHistory(StateSnapshot snapshot)
    {
        var cm = CombatManager.Instance;
        if (cm == null || CmHistoryProperty == null || HistoryEntriesField == null) return;

        var history = CmHistoryProperty.GetValue(cm);
        if (history == null) return;

        var rawEntries = HistoryEntriesField.GetValue(history);
        if (rawEntries is System.Collections.IList entries)
            snapshot._savedHistoryEntries = new List<object>(entries.Cast<object>());
    }

    private static void CaptureRunRng(StateSnapshot snapshot)
    {
        var runManager = RunManager.Instance;
        if (runManager == null) return;

        var runState = RunManagerStateProperty?.GetValue(runManager) as RunState;
        if (runState == null) return;

        var runRngSet = runState.Rng;
        if (runRngSet == null) return;

        var rngsDict = RunRngDictField?.GetValue(runRngSet) as Dictionary<RunRngType, Rng>;
        if (rngsDict == null) return;

        foreach (var kvp in rngsDict)
            snapshot._runRngStates[kvp.Key] = (kvp.Value.Seed, kvp.Value.Counter);
    }

    // ── Restore ──

    public bool Restore(CombatState _) => Restore();

    public bool Restore()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return false;

        var cs = CombatManagerStateField.GetValue(cm) as CombatState;
        if (cs == null) return false;

        ModEntry.Log("=== Restore() starting ===");

        cs.RoundNumber = _roundNumber;
        cs.CurrentSide = _currentSide;

        // Restore creature states
        try
        {
            foreach (var saved in _creatureStates)
            {
                Creature? creature = null;
                foreach (var c in cs.Creatures)
                {
                    if (c.CombatId == saved.CombatId)
                    {
                        creature = c;
                        break;
                    }
                }
                if (creature == null) continue;

                bool wasDead = creature.IsDead;
                CreatureHpField.SetValue(creature, saved.CurrentHp);
                CreatureMaxHpField.SetValue(creature, saved.MaxHp);
                CreatureBlockField.SetValue(creature, saved.Block);
                RestorePowers(creature, saved.Powers);

                if (saved.VisualBodyScale.HasValue)
                {
                    var nCreatureForScale = NCombatRoom.Instance?.GetCreatureNode(creature);
                    var body = nCreatureForScale?.Body;
                    if (body != null)
                        body.Scale = saved.VisualBodyScale.Value;
                }

                if (creature.Monster != null)
                {
                    if (_monsterRngStates.TryGetValue(saved.CombatId, out var rngState))
                        MonsterRngField.SetValue(creature.Monster,
                            new Rng(rngState.seed, rngState.counter));
                    RestoreMonsterMoves(creature.Monster, saved.CombatId);
                }

                if (wasDead && saved.CurrentHp > 0)
                {
                    var combatRoom = NCombatRoom.Instance;
                    if (combatRoom != null)
                    {
                        var nCreature = combatRoom.GetCreatureNode(creature);
                        if (nCreature != null)
                            nCreature.StartReviveAnim();
                        else
                        {
                            combatRoom.AddCreature(creature);
                            combatRoom.GetCreatureNode(creature)?.StartReviveAnim();
                        }
                    }
                }

                if (!wasDead && saved.CurrentHp <= 0)
                    RemoveCreatureVisual(creature);
            }
        }
        catch (Exception ex) { ModEntry.Log($"ERROR in RestoreCreatures: {ex}"); }

        try { ReviveKilledCreatures(cs); }
        catch (Exception ex) { ModEntry.Log($"ERROR in ReviveKilledCreatures: {ex}"); }

        try { RemoveSummonedCreatures(cs); }
        catch (Exception ex) { ModEntry.Log($"ERROR in RemoveSummonedCreatures: {ex}"); }

        // Restore player combat state
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;

            var pcs = player.PlayerCombatState;

            try
            {
                PcsEnergyField.SetValue(pcs, _energy);
                PcsStarsField.SetValue(pcs, _stars);
                RestoreCardPiles(pcs);
                RestoreCardStates(pcs);
            }
            catch (Exception ex) { ModEntry.Log($"ERROR in RestoreCards/Energy: {ex}"); }

            try { RestoreOrbs(pcs); }
            catch (Exception ex) { ModEntry.Log($"ERROR in RestoreOrbs: {ex}"); }

            try { RestorePets(pcs, cs); }
            catch (Exception ex) { ModEntry.Log($"ERROR in RestorePets: {ex}"); }

            try { RestoreRelics(player); }
            catch (Exception ex) { ModEntry.Log($"ERROR in RestoreRelics: {ex}"); }

            try { RestorePotions(player); }
            catch (Exception ex) { ModEntry.Log($"ERROR in RestorePotions: {ex}"); }

            try { RestoreRunDeck(player); }
            catch (Exception ex) { ModEntry.Log($"ERROR in RestoreRunDeck: {ex}"); }

            try
            {
                PlayerGoldField.SetValue(player, _gold);
            }
            catch (Exception ex) { ModEntry.Log($"ERROR in RestoreGold: {ex}"); }
        }

        // Escaped creatures
        try
        {
            if (EscapedCreaturesProp != null)
            {
                var currentEscaped = EscapedCreaturesProp.GetValue(cs);
                if (currentEscaped is List<Creature> escapedList)
                {
                    escapedList.Clear();
                    escapedList.AddRange(_escapedCreatures);
                }
            }
        }
        catch (Exception ex) { ModEntry.Log($"ERROR in RestoreEscapedCreatures: {ex}"); }

        try { RestoreRunRng(); }
        catch (Exception ex) { ModEntry.Log($"ERROR in RestoreRunRng: {ex}"); }

        try { RestoreCombatHistory(); }
        catch (Exception ex) { ModEntry.Log($"ERROR in RestoreCombatHistory: {ex}"); }

        ModEntry.Log("=== Restore() complete ===");
        return true;
    }

    // ── Restore Helpers ──

    private static void RestorePowers(Creature creature, List<PowerData> savedPowers)
    {
        var powersList = (List<PowerModel>)CreaturePowersField.GetValue(creature)!;

        var savedByKey = new Dictionary<ModelId, PowerData>();
        foreach (var p in savedPowers)
            savedByKey[p.Id] = p;

        // Remove powers not in snapshot
        for (int i = powersList.Count - 1; i >= 0; i--)
        {
            if (!savedByKey.ContainsKey(powersList[i].Id))
                powersList.RemoveAt(i);
        }

        // Update existing powers
        var existingIds = new HashSet<ModelId>();
        foreach (var power in powersList)
        {
            if (savedByKey.TryGetValue(power.Id, out var saved))
            {
                PowerAmountField.SetValue(power, saved.Amount);
                PowerAmountOnTurnStartField.SetValue(power, saved.AmountOnTurnStart);
                PowerSkipField.SetValue(power, saved.SkipNextDurationTick);
                if (saved.InternalData != null && PowerInternalDataField != null)
                {
                    var cloned = MemberwiseCloneMethod.Invoke(saved.InternalData, null);
                    PowerInternalDataField.SetValue(power, cloned);
                }
                if (saved.FacingDirection != null && SurroundedFacingField != null)
                    SurroundedFacingField.SetValue(power, saved.FacingDirection);
                existingIds.Add(power.Id);
            }
        }

        // Re-create missing powers
        var ownerField = AccessTools.Field(typeof(PowerModel), "_owner");
        foreach (var saved in savedPowers)
        {
            if (existingIds.Contains(saved.Id)) continue;

            var canonical = ModelDb.GetByIdOrNull<PowerModel>(saved.Id);
            if (canonical == null) continue;

            var newPower = (PowerModel)canonical.MutableClone();
            PowerAmountField.SetValue(newPower, saved.Amount);
            PowerAmountOnTurnStartField.SetValue(newPower, saved.AmountOnTurnStart);
            PowerSkipField.SetValue(newPower, saved.SkipNextDurationTick);
            if (saved.InternalData != null && PowerInternalDataField != null)
            {
                var cloned = MemberwiseCloneMethod.Invoke(saved.InternalData, null);
                PowerInternalDataField.SetValue(newPower, cloned);
            }
            if (saved.FacingDirection != null && SurroundedFacingField != null)
                SurroundedFacingField.SetValue(newPower, saved.FacingDirection);
            ownerField?.SetValue(newPower, creature);
            powersList.Add(newPower);
        }
    }

    private void RestoreCardPiles(PlayerCombatState pcs)
    {
        var stateTracker = CombatManager.Instance?.StateTracker;

        var cardsBefore = new HashSet<CardModel>();
        foreach (var pile in pcs.AllPiles)
            foreach (var card in pile.Cards)
                cardsBefore.Add(card);

        foreach (var pile in pcs.AllPiles)
        {
            if (!_savedPiles.TryGetValue(pile.Type, out var savedCards))
                continue;

            var cardsList = (List<CardModel>)CardPileCardsField.GetValue(pile)!;
            cardsList.Clear();
            cardsList.AddRange(savedCards);
            pile.InvokeContentsChanged();
        }

        var cardsAfter = new HashSet<CardModel>();
        foreach (var pile in pcs.AllPiles)
            foreach (var card in pile.Cards)
                cardsAfter.Add(card);

        // Sync StateTracker subscriptions
        if (stateTracker != null)
        {
            foreach (var card in cardsBefore)
            {
                if (!cardsAfter.Contains(card))
                {
                    try { StateTrackerUnsubscribeCardMethod?.Invoke(stateTracker, new object[] { card }); }
                    catch { }
                }
            }
            foreach (var card in cardsAfter)
            {
                if (!cardsBefore.Contains(card))
                {
                    try { StateTrackerSubscribeCardMethod?.Invoke(stateTracker, new object[] { card }); }
                    catch { }
                }
            }
        }
    }

    private void RestoreCardStates(PlayerCombatState pcs)
    {
        foreach (var card in pcs.AllCards)
        {
            if (!_cardClones.TryGetValue(card, out var clone)) continue;

            foreach (var field in CardMutableFields)
            {
                try { field.SetValue(card, field.GetValue(clone)); }
                catch { }
            }

            card.DynamicVars.InitializeWithOwner(card);

            if (EnergyCostCardField != null && card.EnergyCost != null)
                EnergyCostCardField.SetValue(card.EnergyCost, card);
        }
    }

    private void RestoreMonsterMoves(MonsterModel monster, uint combatId)
    {
        if (!_monsterMoveStates.TryGetValue(combatId, out var saved)) return;

        var sm = monster.MoveStateMachine;
        if (sm == null) return;

        SmPerformedFirstMoveField?.SetValue(sm, saved.PerformedFirstMove);
        MonsterSpawnedField?.SetValue(monster, saved.SpawnedThisTurn);

        var statesProp = SmType != null ? AccessTools.Property(SmType, "States") : null;
        var statesDict = statesProp?.GetValue(sm) as System.Collections.IDictionary;
        if (statesDict == null) return;

        if (saved.CurrentStateId != null && statesDict.Contains(saved.CurrentStateId))
        {
            var currentState = statesDict[saved.CurrentStateId];
            ForceCurrentStateMethod?.Invoke(sm, new[] { currentState });
        }

        var stateLogProp = SmType != null ? AccessTools.Property(SmType, "StateLog") : null;
        if (stateLogProp?.GetValue(sm) is System.Collections.IList stateLog)
        {
            stateLog.Clear();
            foreach (var id in saved.StateLogIds)
            {
                if (statesDict.Contains(id))
                    stateLog.Add(statesDict[id]);
            }
        }

        if (MoveStatePerformedField != null)
        {
            foreach (System.Collections.DictionaryEntry entry in statesDict)
            {
                var key = entry.Key as string;
                if (key != null && MoveStateType != null &&
                    MoveStateType.IsInstanceOfType(entry.Value) &&
                    saved.MovePerformedAtLeastOnce.TryGetValue(key, out var performed))
                {
                    MoveStatePerformedField.SetValue(entry.Value, performed);
                }
            }
        }

        if (saved.NextMoveStateId != null && statesDict.Contains(saved.NextMoveStateId))
        {
            var nextState = statesDict[saved.NextMoveStateId];
            if (MoveStateType != null && MoveStateType.IsInstanceOfType(nextState))
                NextMoveProp?.SetValue(monster, nextState);
        }
    }

    private void RestoreOrbs(PlayerCombatState pcs)
    {
        if (!_hasOrbData) return;

        var orbQueue = pcs.OrbQueue;
        if (orbQueue == null || OrbQueueOrbsField == null) return;

        if (OrbQueueOrbsField.GetValue(orbQueue) is System.Collections.IList orbsList)
        {
            orbsList.Clear();
            foreach (var orb in _savedOrbRefs)
            {
                if (_orbClones.TryGetValue(orb, out var clone))
                    CopyMutableFields(clone, orb, OrbFieldSkipSet);
                orbsList.Add(orb);
            }
        }

        OrbQueueCapacityField?.SetValue(orbQueue, _orbCapacity);
    }

    private void RestorePets(PlayerCombatState pcs, CombatState cs)
    {
        if (PcsPetsField == null) return;
        if (PcsPetsField.GetValue(pcs) is not System.Collections.IList petsList) return;

        petsList.Clear();
        foreach (var id in _petCombatIds)
        {
            foreach (var creature in cs.Creatures)
            {
                if (creature.CombatId == id)
                {
                    petsList.Add(creature);
                    break;
                }
            }
        }
    }

    private void RestoreRelics(Player player)
    {
        var savedByKey = new Dictionary<ModelId, RelicData>();
        foreach (var saved in _relicStates)
            savedByKey[saved.Id] = saved;

        foreach (var relic in player.Relics)
        {
            if (!savedByKey.TryGetValue(relic.Id, out var saved)) continue;

            RelicStackCountField?.SetValue(relic, saved.StackCount);
            relic.IsWax = saved.IsWax;
            relic.IsMelted = saved.IsMelted;
            RelicStatusProperty?.SetValue(relic, saved.Status);

            if (saved.DynamicVarsClone != null)
                RelicDynamicVarsField?.SetValue(relic, saved.DynamicVarsClone);

            // Restore subclass-specific private fields
            if (_relicClones.TryGetValue(relic, out var clone))
                CopyRelicSubclassFields(clone, relic);

            try { InvokeDisplayAmountChangedMethod?.Invoke(relic, null); }
            catch { }
        }
    }

    private static int CopyRelicSubclassFields(object clone, RelicModel target)
    {
        int count = 0;
        var type = target.GetType();
        while (type != null && type != typeof(RelicModel))
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                BindingFlags.DeclaredOnly))
            {
                if (!field.IsLiteral && !field.IsInitOnly)
                {
                    field.SetValue(target, field.GetValue(clone));
                    count++;
                }
            }
            type = type.BaseType;
        }
        return count;
    }

    private void RestorePotions(Player player)
    {
        if (PlayerPotionSlotsField == null) return;

        var slotsObj = PlayerPotionSlotsField.GetValue(player);
        if (slotsObj is not List<PotionModel?> slots) return;

        for (int i = 0; i < slots.Count && i < _potionSlotRefs.Count; i++)
        {
            var originalRef = _potionSlotRefs[i];
            slots[i] = originalRef;
            if (originalRef != null)
            {
                if (_potionClones.TryGetValue(originalRef, out var clone))
                    CopyMutableFields(clone, originalRef, PotionFieldSkipSet);
                PotionOwnerField?.SetValue(originalRef, player);
            }
        }
    }

    private void RestoreCombatHistory()
    {
        if (_savedHistoryEntries == null) return;

        var cm = CombatManager.Instance;
        if (cm == null || CmHistoryProperty == null || HistoryEntriesField == null) return;

        var history = CmHistoryProperty.GetValue(cm);
        if (history == null) return;

        var rawEntries = HistoryEntriesField.GetValue(history);
        if (rawEntries is System.Collections.IList entries)
        {
            entries.Clear();
            foreach (var entry in _savedHistoryEntries)
                entries.Add(entry);
        }
    }

    private void ReviveKilledCreatures(CombatState cs)
    {
        var existingIds = new HashSet<uint>();
        foreach (var c in cs.Creatures)
        {
            if (c.CombatId != null)
                existingIds.Add(c.CombatId.Value);
        }

        foreach (var saved in _creatureStates)
        {
            if (existingIds.Contains(saved.CombatId)) continue;

            var creature = saved.CreatureRef;
            if (creature == null) continue;

            ModEntry.Log($"ReviveKilledCreatures: reviving creature {saved.CombatId}");

            creature.CombatState = cs;
            CreatureHpField.SetValue(creature, saved.CurrentHp);
            CreatureMaxHpField.SetValue(creature, saved.MaxHp);
            CreatureBlockField.SetValue(creature, saved.Block);

            if (creature.Side == CombatSide.Enemy)
            {
                var enemies = CsEnemiesField?.GetValue(cs) as List<Creature>;
                if (enemies != null && !enemies.Contains(creature))
                    enemies.Add(creature);
            }
            else
            {
                var allies = CsAlliesField?.GetValue(cs) as List<Creature>;
                if (allies != null && !allies.Contains(creature))
                    allies.Add(creature);
            }

            if (creature.Monster != null && MonsterMoveStateMachineField != null)
                MonsterMoveStateMachineField.SetValue(creature.Monster, null);

            CombatManager.Instance!.AddCreature(creature);
            RestorePowers(creature, saved.Powers);

            if (creature.Monster != null)
            {
                if (_monsterRngStates.TryGetValue(saved.CombatId, out var rngState))
                    MonsterRngField.SetValue(creature.Monster,
                        new Rng(rngState.seed, rngState.counter));
                RestoreMonsterMoves(creature.Monster, saved.CombatId);
            }

            NCombatRoom.Instance?.AddCreature(creature);
            var nCreature = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (nCreature != null)
            {
                if (saved.VisualGlobalPosition.HasValue)
                    nCreature.GlobalPosition = saved.VisualGlobalPosition.Value;
                nCreature.StartReviveAnim();
            }
        }
    }

    private void RemoveSummonedCreatures(CombatState cs)
    {
        var toRemove = new List<Creature>();
        foreach (var creature in cs.Creatures)
        {
            if (creature.CombatId != null && !_creatureCombatIds.Contains(creature.CombatId.Value))
                toRemove.Add(creature);
        }

        if (toRemove.Count == 0) return;

        var enemies = CsEnemiesField?.GetValue(cs) as List<Creature>;
        var allies = CsAlliesField?.GetValue(cs) as List<Creature>;

        foreach (var creature in toRemove)
        {
            ModEntry.Log($"RemoveSummonedCreatures: removing creature {creature.CombatId}");
            enemies?.Remove(creature);
            allies?.Remove(creature);
            RemoveCreatureVisual(creature);
            CombatManager.Instance?.StateTracker?.Unsubscribe(creature);
            creature.CombatState = null;
        }

        try
        {
            var creaturesChangedDelegate = CsCreaturesChangedField?.GetValue(cs) as Action<CombatState>;
            creaturesChangedDelegate?.Invoke(cs);
        }
        catch { }
    }

    /// <summary>
    /// Player.Deck (런 덱)을 캡처 시점 상태로 복원한다.
    /// RemoveFromDeck으로 제거된 카드를 다시 추가하고,
    /// HasBeenRemovedFromState 플래그를 원래 값으로 되돌린다.
    /// </summary>
    private void RestoreRunDeck(Player player)
    {
        if (_savedDeckCards.Count == 0 && _cardRemovedFlags.Count == 0) return;

        // 1. Player.Deck 카드 목록 복원
        var deckCardsList = CardPileCardsField.GetValue(player.Deck) as List<CardModel>;
        if (deckCardsList != null)
        {
            int oldCount = deckCardsList.Count;
            deckCardsList.Clear();
            deckCardsList.AddRange(_savedDeckCards);
            ModEntry.Log($"RestoreRunDeck: deck {oldCount}->{_savedDeckCards.Count} cards");
        }

        // 2. HasBeenRemovedFromState 플래그 복원
        int flagsRestored = 0;
        foreach (var (deckCard, wasRemoved) in _cardRemovedFlags)
        {
            if (deckCard.HasBeenRemovedFromState != wasRemoved)
            {
                if (CardRemovedFromStateField != null)
                    CardRemovedFromStateField.SetValue(deckCard, wasRemoved);
                else
                    deckCard.HasBeenRemovedFromState = wasRemoved;
                flagsRestored++;
            }
        }
        if (flagsRestored > 0)
            ModEntry.Log($"RestoreRunDeck: restored {flagsRestored} HasBeenRemovedFromState flags");
    }

    private void RestoreRunRng()
    {
        var runManager = RunManager.Instance;
        if (runManager == null) return;

        var runState = RunManagerStateProperty?.GetValue(runManager) as RunState;
        if (runState == null) return;

        var runRngSet = runState.Rng;
        if (runRngSet == null) return;

        var rngsDict = RunRngDictField?.GetValue(runRngSet) as Dictionary<RunRngType, Rng>;
        if (rngsDict == null) return;

        foreach (var (type, (seed, counter)) in _runRngStates)
            rngsDict[type] = new Rng(seed, counter);
    }

    // ── Utility ──

    /// <summary>
    /// Copy all mutable instance fields from source to target,
    /// walking up the type hierarchy. Skips fields in skipSet.
    /// Used for orbs, potions, and other cloneable models.
    /// </summary>
    private static void CopyMutableFields(object source, object target, HashSet<string> skipSet)
    {
        var type = source.GetType();
        while (type != null && type != typeof(object))
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (skipSet.Contains(field.Name)) continue;
                if (field.IsInitOnly || field.IsLiteral) continue;
                try { field.SetValue(target, field.GetValue(source)); }
                catch { }
            }
            type = type.BaseType;
        }
    }

    private static void RemoveCreatureVisual(Creature creature)
    {
        try
        {
            var combatRoom = NCombatRoom.Instance;
            if (combatRoom == null) return;

            var nCreature = combatRoom.GetCreatureNode(creature);

            if (nCreature == null)
            {
                foreach (var nc in combatRoom.RemovingCreatureNodes)
                {
                    if (GodotObject.IsInstanceValid(nc) && nc.Entity == creature)
                    {
                        nCreature = nc;
                        break;
                    }
                }
            }

            if (nCreature == null) return;

            nCreature.Visible = false;
            try { combatRoom.RemoveCreatureNode(nCreature); }
            catch { }
            nCreature.QueueFree();
        }
        catch (Exception ex) { ModEntry.Log($"RemoveCreatureVisual ERROR: {ex}"); }
    }
}
