using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;

namespace UndoMod;

/// <summary>
/// 전투 상태 스냅샷을 저장/복원하는 클래스.
/// CombatState의 핵심 데이터를 깊은 복사로 저장하고,
/// 복원 시 Harmony와 리플렉션으로 원래 상태로 되돌린다.
/// </summary>
public class StateSnapshot
{
    // 플레이어 상태
    public int PlayerHp;
    public int PlayerMaxHp;
    public int PlayerBlock;
    public int Energy;
    public int MaxEnergy;
    public int Stars;
    public int RoundNumber;

    // 카드 더미 (카드 ID 목록)
    public List<CardSnapshot> Hand = new();
    public List<CardSnapshot> DrawPile = new();
    public List<CardSnapshot> DiscardPile = new();
    public List<CardSnapshot> ExhaustPile = new();

    // 적 상태
    public List<EnemySnapshot> Enemies = new();

    // 플레이어 버프
    public List<PowerSnapshot> PlayerPowers = new();

    // 펫
    public List<PetSnapshot> Pets = new();

    // 유물
    public List<RelicSnapshot> Relics = new();

    // 포션
    public List<PotionSnapshot> Potions = new();

    // 구체 (디펙트)
    public List<OrbSnapshot> Orbs = new();
    public int OrbCapacity;

    // RNG 상태 (되돌리기 시 동일한 랜덤 결과 보장)
    public Dictionary<string, int> RunRngCounters = new();  // RunRngType name → Counter
    public string? RunRngStringSeed;
    public Dictionary<string, int> PlayerRngCounters = new(); // PlayerRngType name → Counter
    public uint PlayerRngSeed;
    public Dictionary<int, RngSnapshot> MonsterRngSnapshots = new(); // enemy index → Rng state

    // 전투 기록 (History) 엔트리 수
    public int HistoryEntryCount;

    // 골드
    public int Gold;

    // 도주 크리처 목록
    public List<Creature> EscapedCreatures = new();

    // 스냅샷 캡처 실패 여부
    public bool IsFailed;

    public class RngSnapshot
    {
        public uint Seed;
        public int Counter;
    }

    public class CostModifierSnapshot
    {
        public int Amount;
        public int Type;        // LocalCostType enum as int
        public int Expiration;  // LocalCostModifierExpiration enum as int
        public bool ReduceOnly;
    }

    public class CardSnapshot
    {
        public string Id = "";
        public bool IsUpgraded;
        public int EnergyCost;
        public int EnergyCostBase;  // _base 필드
        public List<CostModifierSnapshot>? CostModifiers;
        public CardModel? CardRef;
        public Godot.Node? HolderRef; // NHandCardHolder reference (핸드 카드용)
        public string? AfflictionId;         // 구속 Entry (예: "Bound", "Hexed" 등)
        public string? AfflictionCategory;   // 구속 Category (예: "AFFLICTION")
        public int AfflictionAmount;         // 구속 수치
        public string? EnchantmentId;        // 인챈트 Entry
        public string? EnchantmentCategory;  // 인챈트 Category
        public int EnchantmentAmount;        // 인챈트 수치 (캡처 시점 값)
        public int EnchantmentStatusVal;    // _status enum 값 (캡처 시점)
        public object? EnchantmentRef;       // 원본 EnchantmentModel 참조
        public Dictionary<string, object?> EnchantmentExtraFields = new(); // 서브클래스 고유 필드 (기세 _extraDamage 등)
        public object? DynamicVarsClone;    // CardModel.DynamicVars의 MemberwiseClone (유전 알고리즘 등 누적 수치)
        public Dictionary<string, object?>? SubclassFields;  // CardModel 서브클래스 고유 필드 (int/bool/decimal)
    }

    // 핸드 카드의 NHandCardHolder 레퍼런스 저장
    public List<Godot.Node> HandHolders = new();

    public class EnemySnapshot
    {
        public int Index;
        public int Hp;
        public int MaxHp;
        public int Block;
        public bool IsAlive;
        public List<PowerSnapshot> Powers = new();
        public Creature? CreatureRef;
        public MoveState? NextMoveRef;  // Monster.NextMove 참조
    }

    public class PowerSnapshot
    {
        public string Id = "";
        public int Amount;
        public PowerModel? PowerRef;  // 원본 파워 참조 (죽은 적 복원용)
        public Dictionary<string, object?> InternalDataFields = new();  // _internalData 내부 필드 캡처
        public object? InternalDataClone;  // _internalData의 MemberwiseClone (원자적 복원용)
    }

    public class PetSnapshot
    {
        public int Hp;
        public int MaxHp;
        public int Block;
        public Creature? CreatureRef;
        public List<PowerSnapshot> Powers = new();
    }

    public class RelicSnapshot
    {
        public string Id = "";
        public int Counter;
        public RelicModel? RelicRef;
        public string? CounterFieldName;
        public Type? CounterFieldDeclaringType;
        public int StatusVal;  // RelicStatus enum as int
        public Dictionary<string, object?> ExtraFields = new(); // 유물별 추가 필드 (bool/int)
        public object? DynamicVarsClone;  // DynamicVars의 MemberwiseClone
    }

    public class PotionSnapshot
    {
        public int Slot;
        public string? PotionId;       // potion Id.Entry (e.g. "strength_potion")
        public PotionModel? PotionRef;  // null이면 빈 슬롯 (fallback용)
    }

    public class OrbSnapshot
    {
        public string Type = "";
        public decimal PassiveVal;
        public decimal EvokeVal;
        public object? OrbRef;  // OrbModel 참조
    }

    private static readonly BindingFlags AllInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>현재 전투 상태에서 스냅샷 생성</summary>
    public static StateSnapshot? Capture(CombatState state)
    {
        try
        {
            var player = state.Players.FirstOrDefault();
            if (player == null) return null;

            var pcs = player.PlayerCombatState;
            var creature = player.Creature;
            if (pcs == null) return null;

            var snap = new StateSnapshot
            {
                PlayerHp = creature.CurrentHp,
                PlayerMaxHp = creature.MaxHp,
                PlayerBlock = creature.Block,
                Energy = pcs.Energy,
                MaxEnergy = pcs.MaxEnergy,
                Stars = pcs.Stars,
                RoundNumber = state.RoundNumber,
            };

            // 핸드
            snap.Hand = CaptureCards(pcs.Hand);
            snap.DrawPile = CaptureCards(pcs.DrawPile);
            snap.DiscardPile = CaptureCards(pcs.DiscardPile);
            snap.ExhaustPile = CaptureCards(pcs.ExhaustPile);

            // NPlayerHand + NSelectedHandCardContainer에서 NHandCardHolder 저장
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree?.Root != null)
                {
                    // NPlayerHand 자식 트리 구조 덤프 (1회)
                    if (!_handTreeLogged)
                    {
                        _handTreeLogged = true;
                        var hn = FindNodeByType(tree.Root, "NPlayerHand");
                        if (hn != null) DumpNodeTree(hn, 0, 3);
                    }

                    // CardHolderContainer에서 홀더 수집 + NCard 씬 경로 캐시
                    var cardHolderContainer = FindNodeByName(tree.Root, "CardHolderContainer");
                    if (cardHolderContainer != null)
                    {
                        for (int i = 0; i < cardHolderContainer.GetChildCount(); i++)
                        {
                            var child = cardHolderContainer.GetChild(i);
                            if (child.GetType().Name == "NHandCardHolder")
                                snap.HandHolders.Add(child);
                        }
                        // NCard 씬 경로를 캐시 (핸드에 카드가 있을 때 항상 갱신)
                        if (_cachedNCardScenePath == null)
                        {
                            var ncard = FindNodeByType(cardHolderContainer, "NCard");
                            if (ncard != null && !string.IsNullOrEmpty(ncard.SceneFilePath))
                            {
                                _cachedNCardScenePath = ncard.SceneFilePath;
                                _cachedNCardType = ncard.GetType();
                            }
                        }
                    }

                    // NSelectedHandCardContainer에서도 (플레이 중인 카드)
                    var selectedContainer = FindNodeByName(tree.Root, "SelectedHandCardContainer");
                    if (selectedContainer != null)
                    {
                        FindAllByType(selectedContainer, "NSelectedHandCardHolder", snap.HandHolders);
                        FindAllByType(selectedContainer, "NHandCardHolder", snap.HandHolders);
                    }

                    ModEntry.Log($"  핸드 홀더 {snap.HandHolders.Count}개 저장됨 (container: {cardHolderContainer?.GetChildCount()}, selected: {selectedContainer?.GetChildCount()})");
                }
            }
            catch { }

            // 플레이어 버프
            foreach (var p in creature.Powers)
            {
                var ps = new PowerSnapshot { Id = p.Id.Entry, Amount = p.Amount, PowerRef = p };
                CapturePowerInternalData(p, ps);
                snap.PlayerPowers.Add(ps);
            }

            // 적
            for (int i = 0; i < state.Enemies.Count; i++)
            {
                var enemy = state.Enemies[i];
                var es = new EnemySnapshot
                {
                    Index = i,
                    Hp = enemy.CurrentHp,
                    MaxHp = enemy.MaxHp,
                    Block = enemy.Block,
                    IsAlive = enemy.IsAlive,
                    CreatureRef = enemy,
                };
                foreach (var p in enemy.Powers)
                {
                    var ps = new PowerSnapshot { Id = p.Id.Entry, Amount = p.Amount, PowerRef = p };
                    CapturePowerInternalData(p, ps);
                    ModEntry.Log($"    적 파워 캡처: {enemy.Name}.{ps.Id} Amount={ps.Amount}" +
                        (ps.InternalDataClone != null ? " [internalData cloned]" : "") +
                        (ps.InternalDataFields.Count > 0 ? $" fields=[{string.Join(", ", ps.InternalDataFields.Select(kv => $"{kv.Key}={kv.Value}"))}]" : ""));
                    es.Powers.Add(ps);
                }
                // NextMove 저장
                try
                {
                    if (enemy.Monster?.NextMove != null)
                        es.NextMoveRef = enemy.Monster.NextMove;
                }
                catch { }
                snap.Enemies.Add(es);
            }

            // 펫
            if (pcs.Pets != null)
            {
                foreach (var pet in pcs.Pets)
                {
                    var petSnap = new PetSnapshot
                    {
                        Hp = pet.CurrentHp,
                        MaxHp = pet.MaxHp,
                        Block = pet.Block,
                        CreatureRef = pet,
                    };
                    try
                    {
                        foreach (var p in pet.Powers)
                        {
                            var ps = new PowerSnapshot { Id = p.Id.Entry, Amount = p.Amount, PowerRef = p };
                            CapturePowerInternalData(p, ps);
                            petSnap.Powers.Add(ps);
                        }
                    }
                    catch { }
                    snap.Pets.Add(petSnap);
                }
            }

            // 포션
            for (int i = 0; i < player.PotionSlots.Count; i++)
            {
                var potion = player.PotionSlots[i];
                snap.Potions.Add(new PotionSnapshot
                {
                    Slot = i,
                    PotionId = potion?.Id.Entry,
                    PotionRef = potion,
                });
            }

            // 유물 카운터 + 내부 상태
            foreach (var relic in player.Relics)
            {
                var rs = new RelicSnapshot
                {
                    Id = relic.Id.Entry,
                    Counter = relic.DisplayAmount,
                    RelicRef = relic,
                };
                // 캡처 시점에 backing 필드를 찾아서 저장 (값이 0이 아닐 때 더 정확)
                FindRelicCounterField(relic, rs);
                // Status 저장
                try
                {
                    var statusProp = relic.GetType().GetProperty("Status", AllInstance);
                    if (statusProp != null)
                    {
                        var statusVal = statusProp.GetValue(relic);
                        if (statusVal != null)
                            rs.StatusVal = (int)Convert.ChangeType(statusVal, typeof(int));
                    }
                }
                catch { }
                // 유물별 bool/int 필드 캡처 (턴 중 변경되는 상태)
                CaptureRelicExtraFields(relic, rs);
                // DynamicVars clone 캡처
                try
                {
                    var dvProp = relic.GetType().GetProperty("DynamicVars", AllInstance);
                    var dv = dvProp?.GetValue(relic);
                    if (dv != null)
                    {
                        var cloneMethod = dv.GetType().GetMethod("MemberwiseClone",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        rs.DynamicVarsClone = cloneMethod?.Invoke(dv, null);
                    }
                }
                catch { }
                snap.Relics.Add(rs);
            }

            // 구체
            if (pcs.OrbQueue != null)
            {
                snap.OrbCapacity = pcs.OrbQueue.Capacity;
                foreach (var orb in pcs.OrbQueue.Orbs)
                {
                    snap.Orbs.Add(new OrbSnapshot
                    {
                        Type = orb.Id.Entry,
                        PassiveVal = orb.PassiveVal,
                        EvokeVal = orb.EvokeVal,
                        OrbRef = orb,
                    });
                }
            }

            // RNG 상태 캡처
            try
            {
                var runState = state.RunState;
                if (runState != null)
                {
                    var rngSet = runState.Rng;  // RunRngSet
                    snap.RunRngStringSeed = rngSet.StringSeed;
                    // _rngs 딕셔너리에서 각 RNG의 Counter 저장
                    var rngsField = rngSet.GetType().GetField("_rngs", AllInstance);
                    if (rngsField?.GetValue(rngSet) is System.Collections.IDictionary rngsDict)
                    {
                        foreach (System.Collections.DictionaryEntry entry in rngsDict)
                        {
                            var rng = entry.Value;
                            var counterProp = rng?.GetType().GetProperty("Counter");
                            if (counterProp != null)
                            {
                                int counter = (int)counterProp.GetValue(rng)!;
                                snap.RunRngCounters[entry.Key!.ToString()!] = counter;
                            }
                        }
                    }
                    ModEntry.Log($"  RunRng 캡처: {string.Join(", ", snap.RunRngCounters.Select(kv => $"{kv.Key}={kv.Value}"))}");
                }

                // PlayerRngSet
                var playerRng = player.GetType().GetProperty("PlayerRng", AllInstance)?.GetValue(player);
                if (playerRng != null)
                {
                    var seedProp = playerRng.GetType().GetProperty("Seed");
                    if (seedProp != null)
                        snap.PlayerRngSeed = (uint)seedProp.GetValue(playerRng)!;

                    var pRngsField = playerRng.GetType().GetField("_rngs", AllInstance);
                    if (pRngsField?.GetValue(playerRng) is System.Collections.IDictionary pRngsDict)
                    {
                        foreach (System.Collections.DictionaryEntry entry in pRngsDict)
                        {
                            var rng = entry.Value;
                            var counterProp = rng?.GetType().GetProperty("Counter");
                            if (counterProp != null)
                            {
                                int counter = (int)counterProp.GetValue(rng)!;
                                snap.PlayerRngCounters[entry.Key!.ToString()!] = counter;
                            }
                        }
                    }
                }

                // 몬스터별 개별 RNG
                for (int i = 0; i < state.Enemies.Count; i++)
                {
                    var enemy = state.Enemies[i];
                    if (enemy.Monster != null)
                    {
                        var monsterRng = enemy.Monster.GetType().GetProperty("Rng", AllInstance)?.GetValue(enemy.Monster);
                        if (monsterRng != null)
                        {
                            var seedProp = monsterRng.GetType().GetProperty("Seed");
                            var counterProp = monsterRng.GetType().GetProperty("Counter");
                            if (seedProp != null && counterProp != null)
                            {
                                snap.MonsterRngSnapshots[i] = new RngSnapshot
                                {
                                    Seed = (uint)seedProp.GetValue(monsterRng)!,
                                    Counter = (int)counterProp.GetValue(monsterRng)!,
                                };
                            }
                        }
                    }
                }
            }
            catch (Exception rngEx)
            {
                ModEntry.Log($"  RNG 캡처 실패 (무시): {rngEx.Message}");
            }

            // 전투 기록 엔트리 수 저장
            try
            {
                var history = CombatManager.Instance?.History;
                if (history != null)
                    snap.HistoryEntryCount = history.Entries.Count();
            }
            catch { }

            // 골드 캡처
            try
            {
                var runState = state.RunState;
                if (runState != null)
                {
                    var goldProp = runState.GetType().GetProperty("Gold", AllInstance);
                    if (goldProp != null)
                        snap.Gold = (int)goldProp.GetValue(runState)!;
                    else
                    {
                        var goldField = runState.GetType().GetField("_gold", AllInstance);
                        if (goldField != null)
                            snap.Gold = (int)goldField.GetValue(runState)!;
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  골드 캡처 실패: {ex.Message}");
            }

            // 도주 크리처 캡처
            try
            {
                var escapedField = state.GetType().GetField("_escapedCreatures", AllInstance);
                if (escapedField?.GetValue(state) is System.Collections.IList escapedList)
                {
                    foreach (var c in escapedList)
                    {
                        if (c is Creature escapedCreature)
                            snap.EscapedCreatures.Add(escapedCreature);
                    }
                    if (snap.EscapedCreatures.Count > 0)
                        ModEntry.Log($"  도주 크리처 캡처: {snap.EscapedCreatures.Count}마리");
                }
            }
            catch { }

            ModEntry.Log($"스냅샷 저장: HP {snap.PlayerHp}, 에너지 {snap.Energy}, 핸드 {snap.Hand.Count}장, 골드 {snap.Gold}, History {snap.HistoryEntryCount}");
            return snap;
        }
        catch (Exception ex)
        {
            ModEntry.Log("스냅샷 저장 실패: " + ex.Message);
            // 실패한 스냅샷은 반환하되 IsFailed 마킹
            var failedSnap = new StateSnapshot { IsFailed = true };
            return failedSnap;
        }
    }

    /// <summary>스냅샷으로부터 전투 상태 복원</summary>
    public bool Restore(CombatState state)
    {
        try
        {
            var player = state.Players.FirstOrDefault();
            if (player == null) return false;

            var pcs = player.PlayerCombatState;
            var creature = player.Creature;
            if (pcs == null) return false;

            // GameAction 타입 덤프 (1회) - Draw/MoveCard 계열 찾기
            if (!_pcsLogged)
            {
                _pcsLogged = true;
                var asm = typeof(CombatManager).Assembly;
                Type[]? allTypes = null;
                try { allTypes = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { allTypes = ex.Types.Where(t => t != null).ToArray()!; }

                // 카드 이동 관련 타입 찾기
                foreach (var t in allTypes)
                {
                    var tn = t.Name.ToLower();
                    if (tn.Contains("draw") || tn.Contains("movecard") || tn.Contains("addtohand") || tn.Contains("cardtohand"))
                    {
                        ModEntry.Log($"  관련 타입: {t.FullName}");
                    }
                }

                // NHand, NCard, CardNode, HandNode 등 핸드 UI 타입 검색
                foreach (var t in allTypes)
                {
                    var tn = t.Name;
                    if (tn == "NHand" || tn == "NHandArea" || tn == "NCardInHand" ||
                        tn == "NPlayerHand" || tn == "NCombatHand" ||
                        (tn.StartsWith("N") && tn.ToLower().Contains("hand") && !tn.ToLower().Contains("map")))
                    {
                        ModEntry.Log($"  핸드 노드 타입: {t.FullName}");
                        foreach (var m in t.GetMethods(AllInstance).Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
                        {
                            var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                            ModEntry.Log($"    M: {m.Name}({parms}) : {m.ReturnType.Name}");
                        }
                    }
                }

                // GameAction 중 카드 이동 관련
                foreach (var t in allTypes)
                {
                    var tn = t.Name.ToLower();
                    if (t.Namespace != null && t.Namespace.Contains("GameActions") &&
                        (tn.Contains("draw") || tn.Contains("hand") || tn.Contains("move") || tn.Contains("return")))
                    {
                        ModEntry.Log($"  카드이동 GameAction: {t.FullName}");
                    }
                }

                // Nodes.Combat 네임스페이스의 모든 타입
                ModEntry.Log("=== Nodes.Combat 타입들 ===");
                foreach (var t in allTypes.Where(t => t.Namespace != null && t.Namespace.Contains("Nodes.Combat")).OrderBy(t => t.Name))
                {
                    ModEntry.Log($"  {t.Name}");
                }
            }

            // 진행 중인 GameAction 파이프라인 클리어
            // 카드 선택 UI 등 중간 상태에서 Undo할 때 필수
            try
            {
                var cmInstance = CombatManager.Instance;
                if (cmInstance != null)
                {
                    // ActionManager / GameActionQueue 접근 시도
                    var cmType = cmInstance.GetType();
                    // CancelCurrentAction, ClearActions 등 찾기
                    var clearActionsMethod = cmType.GetMethod("ClearActions", AllInstance)
                        ?? cmType.GetMethod("ClearActionQueue", AllInstance)
                        ?? cmType.GetMethod("CancelActions", AllInstance);
                    if (clearActionsMethod != null)
                    {
                        clearActionsMethod.Invoke(cmInstance, null);
                        ModEntry.Log("  GameAction 큐 클리어 완료");
                    }
                    else
                    {
                        // 필드로 직접 접근
                        foreach (var f in cmType.GetFields(AllInstance))
                        {
                            var fn = f.Name.ToLower();
                            if (fn.Contains("action") && fn.Contains("queue") || fn.Contains("actionlist"))
                            {
                                var queue = f.GetValue(cmInstance);
                                if (queue != null)
                                {
                                    var clearM = queue.GetType().GetMethod("Clear");
                                    clearM?.Invoke(queue, null);
                                    ModEntry.Log($"  Action 큐 클리어: {f.Name}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  GameAction 클리어 실패 (무시): {ex.Message}");
            }

            // 플레이어 HP/방어도/에너지/턴 복원
            SetProperty(creature, "CurrentHp", PlayerHp);
            SetProperty(creature, "Block", PlayerBlock);
            SetProperty(pcs, "Energy", Energy);
            SetProperty(pcs, "Stars", Stars);
            bool crossTurn = state.RoundNumber != RoundNumber;
            SetProperty(state, "RoundNumber", RoundNumber);

            // CombatManager 턴 플래그 리셋 (같은 턴이라도 undo 후 턴 시스템 정합성 보장)
            try
            {
                var cmInstance = CombatManager.Instance;
                if (cmInstance != null)
                {
                    var cmType = cmInstance.GetType();

                    if (crossTurn)
                    {
                        // 크로스턴: CurrentSide → Player
                        try
                        {
                            var currentSideProp = state.GetType().GetProperty("CurrentSide", AllInstance);
                            if (currentSideProp != null)
                            {
                                var sideEnumVal = Enum.ToObject(currentSideProp.PropertyType, 1); // Player = 1
                                currentSideProp.SetValue(state, sideEnumVal);
                            }
                        }
                        catch { }
                        ModEntry.Log($"  크로스턴 복원: CurrentSide → Player (턴 {RoundNumber})");
                    }

                    // 항상 리셋: 플레이 페이즈 플래그
                    SetProperty(cmInstance, "IsPlayPhase", true);
                    SetProperty(cmInstance, "EndingPlayerTurnPhaseOne", false);
                    SetProperty(cmInstance, "EndingPlayerTurnPhaseTwo", false);
                    SetProperty(cmInstance, "IsEnemyTurnStarted", false);
                    SetField(cmInstance, "_playerActionsDisabled", false);
                    SetProperty(cmInstance, "IsPaused", false);

                    // _playersReadyToEndTurn 클리어
                    var readyField = cmType.GetField("_playersReadyToEndTurn", AllInstance);
                    if (readyField != null)
                    {
                        var hashSet = readyField.GetValue(cmInstance);
                        hashSet?.GetType().GetMethod("Clear")?.Invoke(hashSet, null);
                    }

                    // _playersReadyToBeginEnemyTurn 클리어
                    var readyEnemyField = cmType.GetField("_playersReadyToBeginEnemyTurn", AllInstance);
                    if (readyEnemyField != null)
                    {
                        var hashSet = readyEnemyField.GetValue(cmInstance);
                        hashSet?.GetType().GetMethod("Clear")?.Invoke(hashSet, null);
                    }

                    // 액션 시스템 — 최소한의 보정만 수행 (플레이 페이즈 중 undo면 건드리지 않음)
                    try
                    {
                        var runMgrType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager");
                        var runInstance = runMgrType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                        if (runInstance != null)
                        {
                            var aqsProp = runInstance.GetType().GetProperty("ActionQueueSynchronizer", AllInstance);
                            var aqsInstance = aqsProp?.GetValue(runInstance);
                            if (aqsInstance != null)
                            {
                                // 현재 CombatState 확인
                                var csProperty = aqsInstance.GetType().GetProperty("CombatState", AllInstance);
                                var currentCS = csProperty?.GetValue(aqsInstance);
                                var acsType = typeof(CombatManager).Assembly
                                    .GetType("MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionSynchronizerCombatState");
                                var playPhaseVal = acsType != null ? Enum.Parse(acsType, "PlayPhase") : null;

                                ModEntry.Log($"  ActionQueueSynchronizer.CombatState = {currentCS}");

                                // PlayPhase가 아닌 경우에만 SetCombatState 호출
                                if (currentCS != null && playPhaseVal != null && !currentCS.Equals(playPhaseVal))
                                {
                                    var setCombatState = aqsInstance.GetType().GetMethod("SetCombatState", AllInstance);
                                    setCombatState?.Invoke(aqsInstance, new[] { playPhaseVal });
                                    ModEntry.Log("  ActionQueueSynchronizer → PlayPhase 전환 완료");
                                }

                                // ActionExecutor Unpause (paused 상태일 때만)
                                var execProp = runInstance.GetType().GetProperty("ActionExecutor", AllInstance);
                                var execInstance = execProp?.GetValue(runInstance);
                                if (execInstance != null)
                                {
                                    var isPausedProp = execInstance.GetType().GetProperty("IsPaused", AllInstance);
                                    var isPaused = isPausedProp?.GetValue(execInstance) as bool? ?? false;
                                    if (isPaused)
                                    {
                                        var unpause = execInstance.GetType().GetMethod("Unpause", AllInstance);
                                        unpause?.Invoke(execInstance, null);
                                        ModEntry.Log("  ActionExecutor Unpause 완료");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Log($"  액션 시스템 보정 실패: {ex.InnerException?.Message ?? ex.Message}");
                    }

                    ModEntry.Log($"  턴 플래그 리셋 완료 (crossTurn={crossTurn}, 턴 {RoundNumber})");
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  턴 플래그 리셋 실패: {ex.Message}");
            }

            // PlayerActionsDisabled 리셋 (포션 사용 중 설정될 수 있음)
            try
            {
                // CombatState 필드 덤프 (1회)
                if (!_combatStateLogged)
                {
                    _combatStateLogged = true;
                    var csType = state.GetType();
                    ModEntry.Log("=== CombatState 주요 필드 ===");
                    while (csType != null && csType != typeof(object))
                    {
                        foreach (var f in csType.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                        {
                            var fn = f.Name.ToLower();
                            if (fn.Contains("action") || fn.Contains("disable") || fn.Contains("player") ||
                                fn.Contains("potion") || fn.Contains("turn") || fn.Contains("phase"))
                            {
                                ModEntry.Log($"  F: {f.Name} : {f.FieldType.Name} = {f.GetValue(state)}");
                            }
                        }
                        foreach (var p in csType.GetProperties(AllInstance | BindingFlags.DeclaredOnly))
                        {
                            var pn = p.Name.ToLower();
                            if (pn.Contains("action") || pn.Contains("disable") || pn.Contains("player") ||
                                pn.Contains("potion") || pn.Contains("turn") || pn.Contains("phase"))
                            {
                                try { ModEntry.Log($"  P: {p.Name} : {p.PropertyType.Name} = {p.GetValue(state)}"); }
                                catch { ModEntry.Log($"  P: {p.Name} : {p.PropertyType.Name} = (error)"); }
                            }
                        }
                        csType = csType.BaseType;
                    }
                }

                // CombatManager._playerActionsDisabled 리셋
                var cmInstance2 = CombatManager.Instance;
                if (cmInstance2 != null)
                    SetField(cmInstance2, "_playerActionsDisabled", false);
            }
            catch { }

            // 포션 복원: ModelDb에서 새 인스턴스 생성 + AddPotionInternal로 정식 추가
            try
            {
                // 현재 플레이어 포션 슬롯 확인
                var currentSlots = player.PotionSlots;
                ModEntry.Log($"  포션 복원 시작: 저장={Potions.Count}슬롯, 현재={currentSlots.Count}슬롯");

                // ModelDb.AllPotions 접근
                var modelDbType = typeof(PotionModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.ModelDb");
                if (modelDbType == null)
                {
                    ModEntry.Log("  ModelDb 타입을 찾을 수 없습니다.");
                    // fallback: 직접 슬롯 설정
                    var slotsField = player.GetType().GetField("_potionSlots", AllInstance);
                    var slotsList = slotsField?.GetValue(player) as System.Collections.IList;
                    if (slotsList != null)
                        for (int i = 0; i < Potions.Count && i < slotsList.Count; i++)
                            slotsList[i] = Potions[i].PotionRef;
                }
                else
                {
                    var allPotionsProp = modelDbType.GetProperty("AllPotions",
                        BindingFlags.Static | BindingFlags.Public);
                    var allPotions = allPotionsProp?.GetValue(null) as System.Collections.IEnumerable;

                    // AddPotionInternal 메서드 찾기
                    var addPotionMethod = player.GetType().GetMethod("AddPotionInternal", AllInstance);
                    if (addPotionMethod == null)
                    {
                        // 상위 타입에서 검색
                        var pt = player.GetType();
                        while (pt != null && addPotionMethod == null)
                        {
                            addPotionMethod = pt.GetMethod("AddPotionInternal",
                                AllInstance | BindingFlags.DeclaredOnly);
                            pt = pt.BaseType;
                        }
                    }
                    ModEntry.Log($"  AddPotionInternal: {(addPotionMethod != null ? "발견" : "없음")}");
                    ModEntry.Log($"  AllPotions: {(allPotions != null ? "발견" : "없음")}");

                    // 1회만: AddPotionInternal 시그니처 로깅
                    if (addPotionMethod != null && !_potionMethodLogged)
                    {
                        _potionMethodLogged = true;
                        var parms = string.Join(", ", addPotionMethod.GetParameters()
                            .Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        ModEntry.Log($"  AddPotionInternal({parms})");
                    }

                    // NPotionContainer 접근 (UI 제거용)
                    var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                    var potionContainer = tree?.Root != null
                        ? FindNodeByType(tree.Root, "NPotionContainer") : null;
                    var discardMethod = potionContainer?.GetType().GetMethod("Discard", AllInstance);
                    var removeUsedMethod = potionContainer?.GetType().GetMethod("RemoveUsed", AllInstance);

                    // 먼저 현재 슬롯에서 사용된 포션을 제거
                    var slotsField = player.GetType().GetField("_potionSlots", AllInstance);
                    var slotsList = slotsField?.GetValue(player) as System.Collections.IList;

                    for (int i = 0; i < Potions.Count && i < currentSlots.Count; i++)
                    {
                        var currentPotion = currentSlots[i];
                        var savedId = Potions[i].PotionId;

                        // 현재 상태와 저장 상태가 같으면 스킵
                        if (savedId == null && currentPotion == null) continue;
                        if (savedId != null && currentPotion != null
                            && currentPotion.Id.Entry == savedId
                            && !currentPotion.HasBeenRemovedFromState) continue;

                        // 현재 슬롯에 포션이 있으면 UI에서 제거
                        if (currentPotion != null && potionContainer != null)
                        {
                            try
                            {
                                if (currentPotion.HasBeenRemovedFromState && removeUsedMethod != null)
                                    removeUsedMethod.Invoke(potionContainer, new object[] { currentPotion });
                                else if (discardMethod != null)
                                    discardMethod.Invoke(potionContainer, new object[] { currentPotion });
                            }
                            catch (Exception ex)
                            {
                                ModEntry.Log($"  포션 UI 제거 실패 슬롯{i}: {ex.Message}");
                            }
                        }

                        // 슬롯 비우기
                        if (slotsList != null && i < slotsList.Count)
                            slotsList[i] = null;

                        // 저장된 포션이 있으면 새로 생성하여 추가
                        if (savedId != null)
                        {
                            PotionModel? freshPotion = null;

                            // ModelDb에서 canonical 포션 찾기
                            if (allPotions != null)
                            {
                                foreach (var p in allPotions)
                                {
                                    var pm = p as PotionModel;
                                    if (pm != null && pm.Id.Entry == savedId)
                                    {
                                        // ToMutable()로 새 인스턴스 생성
                                        var toMutableM = pm.GetType().GetMethod("ToMutable", AllInstance);
                                        if (toMutableM == null)
                                        {
                                            // 상위 클래스에서 검색
                                            var mType = pm.GetType();
                                            while (mType != null && toMutableM == null)
                                            {
                                                toMutableM = mType.GetMethod("ToMutable",
                                                    AllInstance | BindingFlags.DeclaredOnly);
                                                mType = mType.BaseType;
                                            }
                                        }
                                        if (toMutableM != null)
                                        {
                                            freshPotion = toMutableM.Invoke(pm, null) as PotionModel;
                                            ModEntry.Log($"  포션 새로 생성: {savedId} (ToMutable)");
                                        }
                                        break;
                                    }
                                }
                            }

                            if (freshPotion != null)
                            {
                                // AddPotionInternal(potion, slotIndex, silent)로 정식 추가
                                if (addPotionMethod != null)
                                {
                                    try
                                    {
                                        var paramCount = addPotionMethod.GetParameters().Length;
                                        if (paramCount == 3)
                                            addPotionMethod.Invoke(player, new object[] { freshPotion, i, false });
                                        else if (paramCount == 2)
                                            addPotionMethod.Invoke(player, new object[] { freshPotion, i });
                                        else
                                            addPotionMethod.Invoke(player, new object[] { freshPotion });
                                        ModEntry.Log($"  포션 추가 완료: 슬롯{i} ← {savedId} (AddPotionInternal)");
                                    }
                                    catch (Exception ex)
                                    {
                                        ModEntry.Log($"  AddPotionInternal 실패: {ex.Message}");
                                        // fallback: 직접 슬롯에 넣기
                                        if (slotsList != null && i < slotsList.Count)
                                            slotsList[i] = freshPotion;
                                    }
                                }
                                else
                                {
                                    // AddPotionInternal 없으면 직접 슬롯에 넣기 + UI Add
                                    if (slotsList != null && i < slotsList.Count)
                                        slotsList[i] = freshPotion;
                                    var addMethod = potionContainer?.GetType().GetMethod("Add", AllInstance);
                                    addMethod?.Invoke(potionContainer, new object[] { freshPotion, false });
                                    ModEntry.Log($"  포션 추가 완료: 슬롯{i} ← {savedId} (fallback Add)");
                                }
                            }
                            else
                            {
                                ModEntry.Log($"  포션 생성 실패: {savedId} - ModelDb에서 찾을 수 없음");
                                // 최후 fallback: 원본 참조 사용
                                if (Potions[i].PotionRef != null && slotsList != null && i < slotsList.Count)
                                {
                                    var orig = Potions[i].PotionRef;
                                    SetProperty(orig, "HasBeenRemovedFromState", false);
                                    SetProperty(orig, "IsQueued", false);
                                    slotsList[i] = orig;
                                    var addMethod = potionContainer?.GetType().GetMethod("Add", AllInstance);
                                    addMethod?.Invoke(potionContainer, new object[] { orig, false });
                                    ModEntry.Log($"  포션 fallback 추가: 슬롯{i} ← {savedId}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  포션 복원 실패: {ex.Message}\n{ex.StackTrace}");
            }

            // 유물 카운터 + 내부 상태 복원
            foreach (var rs in Relics)
            {
                if (rs.RelicRef == null) continue;
                if (rs.RelicRef.DisplayAmount != rs.Counter)
                {
                    ModEntry.Log($"  유물: {rs.Id}, 저장={rs.Counter}, 현재={rs.RelicRef.DisplayAmount}");
                    RestoreRelicCounter(rs);
                    ModEntry.Log($"    설정 후: {rs.RelicRef.DisplayAmount}");
                }
                // Status 복원
                try
                {
                    var statusProp = rs.RelicRef.GetType().GetProperty("Status", AllInstance);
                    if (statusProp != null)
                    {
                        var currentStatus = (int)Convert.ChangeType(statusProp.GetValue(rs.RelicRef)!, typeof(int));
                        if (currentStatus != rs.StatusVal)
                        {
                            var statusEnumVal = Enum.ToObject(statusProp.PropertyType, rs.StatusVal);
                            statusProp.SetValue(rs.RelicRef, statusEnumVal);
                            ModEntry.Log($"  유물 Status 복원: {rs.Id}, {currentStatus} → {rs.StatusVal}");
                        }
                    }
                }
                catch (Exception ex) { ModEntry.Log($"  유물 Status 복원 실패 ({rs.Id}): {ex.Message}"); }
                // 추가 필드 복원
                RestoreRelicExtraFields(rs);
                // DynamicVars 복원
                if (rs.DynamicVarsClone != null)
                {
                    try
                    {
                        var dvProp = rs.RelicRef.GetType().GetProperty("DynamicVars", AllInstance);
                        if (dvProp != null && dvProp.CanWrite)
                        {
                            // 스냅샷의 clone을 다시 clone해서 설정 (스냅샷 오염 방지)
                            var cloneMethod = rs.DynamicVarsClone.GetType().GetMethod("MemberwiseClone",
                                BindingFlags.Instance | BindingFlags.NonPublic);
                            var freshClone = cloneMethod?.Invoke(rs.DynamicVarsClone, null);
                            if (freshClone != null)
                            {
                                dvProp.SetValue(rs.RelicRef, freshClone);
                                ModEntry.Log($"  유물 DynamicVars 복원: {rs.Id}");
                            }
                        }
                        else
                        {
                            // setter 없으면 backing field 시도
                            SetField(rs.RelicRef, "<DynamicVars>k__BackingField", rs.DynamicVarsClone);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Log($"  유물 DynamicVars 복원 실패 ({rs.Id}): {ex.Message}");
                    }
                }
            }

            // 핸드 이외의 더미 복원 (UI 차이 계산을 위해 현재 카운트 보존)
            int preDrawCount = pcs.DrawPile.Cards.Count;
            int preDiscardCount = pcs.DiscardPile.Cards.Count;
            int preExhaustCount = pcs.ExhaustPile.Cards.Count;

            RestoreCardPile(pcs.DrawPile, DrawPile);
            RestoreCardPile(pcs.DiscardPile, DiscardPile);
            RestoreCardPile(pcs.ExhaustPile, ExhaustPile);

            // 모든 더미 카드들의 Godot NCard 이벤트 구독 제거
            // (NCard가 dispose/QueueFree되었지만 CardModel.AfflictionChanged/EnchantmentChanged에 구독이 남아 있으면
            //  ChainsOfBindingPower.AfterCardDrawn 등에서 ObjectDisposedException 발생)
            ClearAllNCardSubscriptions(pcs.DrawPile);
            ClearAllNCardSubscriptions(pcs.DiscardPile);
            ClearAllNCardSubscriptions(pcs.ExhaustPile);
            ClearAllNCardSubscriptions(pcs.Hand); // 핸드도 정리 (UI 복원에서 재구독됨)

            // 핸드 복원: 현재 핸드에서 없어야 할 카드 제거, 있어야 할 카드 추가
            RestoreHand(pcs, Hand);

            // 카드 강화 상태 복원 (전투장비 등으로 강화된 카드 되돌리기)
            RestoreCardUpgradeState(Hand);

            // 카드 구속(affliction) 상태 복원
            RestoreCardAfflictions(Hand);
            RestoreCardAfflictions(DrawPile);
            RestoreCardAfflictions(DiscardPile);
            RestoreCardAfflictions(ExhaustPile);

            // 카드 인챈트(enchantment) 상태 복원
            RestoreCardEnchantments(Hand);
            RestoreCardEnchantments(DrawPile);
            RestoreCardEnchantments(DiscardPile);
            RestoreCardEnchantments(ExhaustPile);

            // 카드 비용 수정자 복원
            RestoreCardCostModifiers(Hand);
            RestoreCardCostModifiers(DrawPile);
            RestoreCardCostModifiers(DiscardPile);
            RestoreCardCostModifiers(ExhaustPile);

            // 카드 DynamicVars + 서브클래스 필드 복원 (유전 알고리즘 등 누적 수치)
            RestoreCardDynamicState(Hand);
            RestoreCardDynamicState(DrawPile);
            RestoreCardDynamicState(DiscardPile);
            RestoreCardDynamicState(ExhaustPile);

            // 플레이어 파워 복원
            RestorePowers(creature, PlayerPowers);

            // 구체 복원 (디펙트)
            RestoreOrbs(pcs);

            // ChainsOfBindingPower의 boundCardPlayed 플래그는 RestorePowerInternalData에서
            // 스냅샷 값으로 복원됨 (별도 리셋 불필요 - 강제 false는 구속 우회 버그 유발)

            // 스냅샷에 없는 적 제거 (턴 사이에 소환된 부하 등)
            {
                var savedCreatureRefs = new HashSet<Creature>(Enemies.Where(e => e.CreatureRef != null).Select(e => e.CreatureRef!));
                var currentEnemies = state.Enemies.ToList();
                foreach (var enemy in currentEnemies)
                {
                    if (!savedCreatureRefs.Contains(enemy) && enemy.IsAlive)
                    {
                        ModEntry.Log($"  스냅샷에 없는 적 제거: {enemy.Name} (소환된 적)");
                        try
                        {
                            // HP를 0으로 설정하여 죽이기
                            SetProperty(enemy, "CurrentHp", 0);

                            // _enemies 리스트에서 직접 제거
                            var enemiesField = state.GetType().GetField("_enemies", AllInstance);
                            if (enemiesField?.GetValue(state) is System.Collections.IList enemiesList)
                            {
                                enemiesList.Remove(enemy);
                                ModEntry.Log($"    _enemies에서 제거 완료");
                            }

                            // UI 노드(NCreature)도 제거 — NCombatRoom 리스트에서도 정리
                            try
                            {
                                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                                if (tree?.Root != null)
                                {
                                    var combatRoomNode = FindNodeByType(tree.Root, "NCombatRoom");
                                    if (combatRoomNode != null)
                                    {
                                        // _creatureNodes 리스트에서 해당 적의 노드 제거
                                        var creatureNodesField = combatRoomNode.GetType().GetField("_creatureNodes", AllInstance);
                                        if (creatureNodesField?.GetValue(combatRoomNode) is System.Collections.IList creatureNodes)
                                        {
                                            for (int ci = creatureNodes.Count - 1; ci >= 0; ci--)
                                            {
                                                var node = creatureNodes[ci] as Godot.Node;
                                                if (node == null || !Godot.GodotObject.IsInstanceValid(node)) continue;
                                                var entityProp = node.GetType().GetProperty("Entity", AllInstance);
                                                var entity = entityProp?.GetValue(node) as Creature;
                                                if (entity == enemy)
                                                {
                                                    creatureNodes.RemoveAt(ci);
                                                    if (node.GetParent() != null)
                                                        node.GetParent().RemoveChild(node);
                                                    node.QueueFree();
                                                    ModEntry.Log($"    _creatureNodes에서 NCreature 제거 완료");
                                                }
                                            }
                                        }

                                        // _removingCreatureNodes에서도 정리
                                        var removingField = combatRoomNode.GetType().GetField("_removingCreatureNodes", AllInstance);
                                        if (removingField?.GetValue(combatRoomNode) is System.Collections.IList removingNodes)
                                        {
                                            for (int ri = removingNodes.Count - 1; ri >= 0; ri--)
                                            {
                                                var node = removingNodes[ri] as Godot.Node;
                                                if (node == null || !Godot.GodotObject.IsInstanceValid(node)) continue;
                                                var entityProp = node.GetType().GetProperty("Entity", AllInstance);
                                                var entity = entityProp?.GetValue(node) as Creature;
                                                if (entity == enemy)
                                                {
                                                    removingNodes.RemoveAt(ri);
                                                    if (node.GetParent() != null)
                                                        node.GetParent().RemoveChild(node);
                                                    node.QueueFree();
                                                    ModEntry.Log($"    _removingCreatureNodes에서 NCreature 제거 완료");
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // fallback: NCombatRoom 못 찾으면 직접 검색
                                        var nCreature = FindNCreatureForMonster(tree.Root, enemy);
                                        if (nCreature != null)
                                        {
                                            if (nCreature.GetParent() != null)
                                                nCreature.GetParent().RemoveChild(nCreature);
                                            nCreature.QueueFree();
                                            ModEntry.Log($"    NCreature 직접 제거 완료 (fallback)");
                                        }
                                    }
                                }
                            }
                            catch (Exception uiEx)
                            {
                                ModEntry.Log($"    NCreature UI 제거 중 예외: {uiEx.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            ModEntry.Log($"    소환적 제거 실패: {ex.Message}");
                        }
                    }
                }
            }

            // 적 상태 복원
            foreach (var es in Enemies)
            {
                if (es.CreatureRef != null)
                {
                    bool wasDead = es.CreatureRef.IsDead;
                    bool shouldBeAlive = es.Hp > 0;

                    // 죽은 적 → CombatState 먼저 복원 (파워 복원에 필요)
                    if (wasDead && shouldBeAlive)
                    {
                        var cs = CombatManager.Instance?.DebugOnlyGetState();
                        if (cs != null && es.CreatureRef.CombatState == null)
                        {
                            SetProperty(es.CreatureRef, "CombatState", cs);
                        }
                    }

                    SetProperty(es.CreatureRef, "CurrentHp", es.Hp);
                    SetProperty(es.CreatureRef, "Block", es.Block);
                    RestorePowers(es.CreatureRef, es.Powers);

                    // NextMove 복원 (의도 표시) — SetMoveImmediate 사용
                    if (es.NextMoveRef != null && es.CreatureRef.Monster != null)
                    {
                        try
                        {
                            // SetMoveImmediate(MoveState, forceTransition: true)
                            // → NextMove 설정 + MoveStateMachine.ForceCurrentState + RefreshIntents UI 갱신
                            es.CreatureRef.Monster.SetMoveImmediate(es.NextMoveRef, forceTransition: true);
                            ModEntry.Log($"  적 NextMove 복원 (SetMoveImmediate): {es.CreatureRef.Name}");
                        }
                        catch (Exception ex)
                        {
                            ModEntry.Log($"  적 NextMove 복원 실패 ({es.CreatureRef.Name}): {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }

                    // 죽은 적을 살려야 하는 경우 → CombatState 등록 + NCreature 노드 재생성
                    if (wasDead && shouldBeAlive)
                    {
                        try
                        {
                            ReviveEnemy(es.CreatureRef);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.Log($"  적 부활 실패 ({es.CreatureRef.Name}): {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                }
            }

            // 펫 복원
            foreach (var ps in Pets)
            {
                if (ps.CreatureRef != null)
                {
                    SetField(ps.CreatureRef, "_maxHp", ps.MaxHp);
                    SetProperty(ps.CreatureRef, "CurrentHp", ps.Hp);
                    SetProperty(ps.CreatureRef, "Block", ps.Block);
                }
            }

            // 전투 기록(History) 복원 — 스냅샷 이후에 추가된 항목 제거
            try
            {
                var history = CombatManager.Instance?.History;
                if (history != null)
                {
                    var entriesField = history.GetType().GetField("_entries", AllInstance);
                    if (entriesField?.GetValue(history) is System.Collections.IList entries)
                    {
                        int currentCount = entries.Count;
                        if (currentCount > HistoryEntryCount)
                        {
                            // RemoveRange로 스냅샷 이후 항목 제거
                            var removeRange = entries.GetType().GetMethod("RemoveRange");
                            if (removeRange != null)
                            {
                                removeRange.Invoke(entries, new object[] { HistoryEntryCount, currentCount - HistoryEntryCount });
                                ModEntry.Log($"  History 복원: {currentCount} → {HistoryEntryCount} ({currentCount - HistoryEntryCount}개 제거)");

                                // Changed 이벤트 발화 → CombatStateTracker가 UI 갱신
                                var changedEvt = history.GetType().GetField("Changed", AllInstance);
                                var handler = changedEvt?.GetValue(history) as Action;
                                handler?.Invoke();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  History 복원 실패: {ex.Message}");
            }

            // RNG 상태 복원
            try
            {
                var runState = state.RunState;
                if (runState != null && RunRngCounters.Count > 0)
                {
                    var rngSet = runState.Rng;
                    var rngsField = rngSet.GetType().GetField("_rngs", AllInstance);
                    if (rngsField?.GetValue(rngSet) is System.Collections.IDictionary rngsDict)
                    {
                        // RunRngType enum 타입 가져오기
                        var rngType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Entities.Rngs.RunRngType");
                        foreach (var kvp in RunRngCounters)
                        {
                            try
                            {
                                // enum 파싱
                                var enumVal = Enum.Parse(rngType!, kvp.Key);
                                if (rngsDict.Contains(enumVal))
                                {
                                    var currentRng = rngsDict[enumVal];
                                    var counterProp = currentRng!.GetType().GetProperty("Counter");
                                    int currentCounter = (int)counterProp!.GetValue(currentRng)!;

                                    if (kvp.Value < currentCounter)
                                    {
                                        // 카운터가 뒤로 가야 하므로 새 Rng 인스턴스 생성
                                        var seedProp = currentRng.GetType().GetProperty("Seed");
                                        uint seed = (uint)seedProp!.GetValue(currentRng)!;
                                        var rngCtor = currentRng.GetType().GetConstructor(new[] { typeof(uint), typeof(int) });
                                        var newRng = rngCtor!.Invoke(new object[] { seed, kvp.Value });
                                        rngsDict[enumVal] = newRng;
                                    }
                                    else if (kvp.Value > currentCounter)
                                    {
                                        // 앞으로만 → FastForwardCounter
                                        var ffMethod = currentRng.GetType().GetMethod("FastForwardCounter");
                                        ffMethod!.Invoke(currentRng, new object[] { kvp.Value });
                                    }
                                }
                            }
                            catch { }
                        }
                        ModEntry.Log($"  RunRng 복원 완료");
                    }
                }

                // PlayerRngSet 복원
                if (PlayerRngCounters.Count > 0)
                {
                    var playerRng = player.GetType().GetProperty("PlayerRng", AllInstance)?.GetValue(player);
                    if (playerRng != null)
                    {
                        var pRngsField = playerRng.GetType().GetField("_rngs", AllInstance);
                        if (pRngsField?.GetValue(playerRng) is System.Collections.IDictionary pRngsDict)
                        {
                            var pRngType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Entities.Rngs.PlayerRngType");
                            foreach (var kvp in PlayerRngCounters)
                            {
                                try
                                {
                                    var enumVal = Enum.Parse(pRngType!, kvp.Key);
                                    if (pRngsDict.Contains(enumVal))
                                    {
                                        var currentRng = pRngsDict[enumVal];
                                        var counterProp = currentRng!.GetType().GetProperty("Counter");
                                        int currentCounter = (int)counterProp!.GetValue(currentRng)!;

                                        if (kvp.Value < currentCounter)
                                        {
                                            var seedProp = currentRng.GetType().GetProperty("Seed");
                                            uint seed = (uint)seedProp!.GetValue(currentRng)!;
                                            var rngCtor = currentRng.GetType().GetConstructor(new[] { typeof(uint), typeof(int) });
                                            var newRng = rngCtor!.Invoke(new object[] { seed, kvp.Value });
                                            pRngsDict[enumVal] = newRng;
                                        }
                                        else if (kvp.Value > currentCounter)
                                        {
                                            var ffMethod = currentRng.GetType().GetMethod("FastForwardCounter");
                                            ffMethod!.Invoke(currentRng, new object[] { kvp.Value });
                                        }
                                    }
                                }
                                catch { }
                            }
                            ModEntry.Log($"  PlayerRng 복원 완료");
                        }
                    }
                }

                // 몬스터 개별 RNG 복원
                foreach (var kvp in MonsterRngSnapshots)
                {
                    try
                    {
                        if (kvp.Key < state.Enemies.Count)
                        {
                            var enemy = state.Enemies[kvp.Key];
                            if (enemy.Monster != null)
                            {
                                var rngProp = enemy.Monster.GetType().GetProperty("Rng", AllInstance);
                                if (rngProp != null)
                                {
                                    var rngSnapType = typeof(MegaCrit.Sts2.Core.Random.Rng);
                                    var rngCtor = rngSnapType.GetConstructor(new[] { typeof(uint), typeof(int) });
                                    var newRng = rngCtor!.Invoke(new object[] { kvp.Value.Seed, kvp.Value.Counter });
                                    rngProp.SetValue(enemy.Monster, newRng);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception rngEx)
            {
                ModEntry.Log($"  RNG 복원 실패 (무시): {rngEx.Message}");
            }

            // 골드 복원
            try
            {
                var runState = state.RunState;
                if (runState != null && Gold > 0)
                {
                    var goldProp = runState.GetType().GetProperty("Gold", AllInstance);
                    if (goldProp != null && goldProp.CanWrite)
                    {
                        var currentGold = (int)goldProp.GetValue(runState)!;
                        if (currentGold != Gold)
                        {
                            goldProp.SetValue(runState, Gold);
                            ModEntry.Log($"  골드 복원: {currentGold} → {Gold}");
                        }
                    }
                    else
                    {
                        SetField(runState, "_gold", Gold);
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  골드 복원 실패: {ex.Message}");
            }

            // 도주 크리처 복원
            try
            {
                var escapedField = state.GetType().GetField("_escapedCreatures", AllInstance);
                if (escapedField?.GetValue(state) is System.Collections.IList escapedList)
                {
                    int currentCount = escapedList.Count;
                    escapedList.Clear();
                    foreach (var c in EscapedCreatures)
                        escapedList.Add(c);
                    if (currentCount != EscapedCreatures.Count)
                        ModEntry.Log($"  도주 크리처 복원: {currentCount} → {EscapedCreatures.Count}");
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  도주 크리처 복원 실패: {ex.Message}");
            }

            // 카드 더미 UI 버튼 직접 갱신
            RefreshPileButtons(pcs, preDrawCount, preDiscardCount, preExhaustCount);

            // EndTurnButton UI 상태 리셋
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree?.Root != null)
                {
                    var btn = FindNodeByType(tree.Root, "NEndTurnButton");
                    if (btn != null)
                    {
                        // Disabled/Interactable 리셋
                        var disabledProp = btn.GetType().GetProperty("Disabled", AllInstance);
                        if (disabledProp != null && disabledProp.CanWrite)
                            disabledProp.SetValue(btn, false);

                        // Visible 보장
                        var visibleProp = btn.GetType().GetProperty("Visible", AllInstance);
                        if (visibleProp != null && visibleProp.CanWrite)
                            visibleProp.SetValue(btn, true);

                        // Refresh 호출 (있으면)
                        var refreshMethod = btn.GetType().GetMethod("Refresh", AllInstance)
                            ?? btn.GetType().GetMethod("UpdateState", AllInstance);
                        refreshMethod?.Invoke(btn, null);

                        ModEntry.Log("  EndTurnButton UI 리셋 완료");
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  EndTurnButton 리셋 실패: {ex.Message}");
            }

            // 핸드 카드 정렬 (SortCards)
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree?.Root != null)
                {
                    var handNode = FindNodeByType(tree.Root, "NPlayerHand");
                    if (handNode != null)
                    {
                        var sortMethod = handNode.GetType().GetMethod("SortCards", AllInstance)
                            ?? handNode.GetType().GetMethod("ArrangeCards", AllInstance)
                            ?? handNode.GetType().GetMethod("LayoutCards", AllInstance);
                        sortMethod?.Invoke(handNode, null);
                        if (sortMethod != null)
                            ModEntry.Log("  핸드 카드 정렬 완료");
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  핸드 카드 정렬 실패: {ex.Message}");
            }

            // 모든 크리처의 NPowerContainer UI 강제 갱신
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree?.Root != null)
                {
                    var combatRoomNode = FindNodeByType(tree.Root, "NCombatRoom");
                    if (combatRoomNode != null)
                    {
                        // 모든 NCreature 노드에서 NPowerContainer.RefreshAll 호출
                        var creatureNodesField = combatRoomNode.GetType().GetField("_creatureNodes", AllInstance);
                        if (creatureNodesField?.GetValue(combatRoomNode) is System.Collections.IList creatureNodes)
                        {
                            foreach (var cn in creatureNodes)
                            {
                                if (cn is not Godot.Node cNode || !Godot.GodotObject.IsInstanceValid(cNode)) continue;
                                try
                                {
                                    // NCreature → NPowerContainer
                                    var powerContainerProp = cNode.GetType().GetProperty("PowerContainer", AllInstance);
                                    var powerContainer = powerContainerProp?.GetValue(cNode) as Godot.Node;
                                    if (powerContainer != null)
                                    {
                                        var refreshAll = powerContainer.GetType().GetMethod("RefreshAll", AllInstance);
                                        refreshAll?.Invoke(powerContainer, null);
                                    }

                                    // NCreature → NHealthBar 갱신
                                    var refreshHealth = cNode.GetType().GetMethod("RefreshHealth", AllInstance)
                                        ?? cNode.GetType().GetMethod("UpdateHealth", AllInstance);
                                    refreshHealth?.Invoke(cNode, null);
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  NPowerContainer 갱신 실패: {ex.Message}");
            }

            // === 진단 덤프: 복원 후 전체 시스템 상태 ===
            DumpSystemState("Restore 완료 후", state, crossTurn);

            ModEntry.Log($"스냅샷 복원: HP {PlayerHp}, 에너지 {Energy}, 핸드 {Hand.Count}장, 골드 {Gold}");
            return true;
        }
        catch (Exception ex)
        {
            ModEntry.Log("스냅샷 복원 실패: " + ex.Message);
            return false;
        }
    }

    private static bool _cardFieldsDumped;
    private static List<CardSnapshot> CaptureCards(CardPile? pile)
    {
        if (pile == null) return new();
        return pile.Cards.Select(c => {
            // CardModel 내부 구조 진단 (첫 번째 카드 1회)
            if (!_cardFieldsDumped)
            {
                _cardFieldsDumped = true;
                ModEntry.Log($"=== CardModel 진단: {c.Id.Entry} (타입: {c.GetType().FullName}) ===");
                var t = c.GetType();
                while (t != null && t != typeof(object))
                {
                    ModEntry.Log($"  --- {t.Name} 필드 ---");
                    foreach (var f in t.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        try { ModEntry.Log($"    F: {f.Name} : {f.FieldType.Name} = {f.GetValue(c)}"); }
                        catch { ModEntry.Log($"    F: {f.Name} : {f.FieldType.Name} = (읽기 실패)"); }
                    }
                    t = t.BaseType;
                }
                foreach (var p in c.GetType().GetProperties(AllInstance))
                {
                    try
                    {
                        if (p.CanRead && (p.PropertyType == typeof(int) || p.PropertyType == typeof(decimal)
                            || p.PropertyType == typeof(bool) || p.PropertyType == typeof(float)))
                            ModEntry.Log($"    P: {p.Name} : {p.PropertyType.Name} = {p.GetValue(c)}");
                    }
                    catch { }
                }
                try
                {
                    var dvProp = c.GetType().GetProperty("DynamicVars", AllInstance);
                    var dv = dvProp?.GetValue(c);
                    ModEntry.Log($"    DynamicVars = {(dv == null ? "null" : dv.GetType().FullName)}");
                    if (dv != null)
                    {
                        foreach (var f in dv.GetType().GetFields(AllInstance))
                        {
                            try { ModEntry.Log($"      DV.{f.Name} : {f.FieldType.Name} = {f.GetValue(dv)}"); }
                            catch { }
                        }
                    }
                }
                catch { }
                ModEntry.Log("=== CardModel 진단 끝 ===");
            }
            var cs = new CardSnapshot
            {
                Id = c.Id.Entry,
                IsUpgraded = c.IsUpgraded,
                EnergyCost = c.EnergyCost.GetWithModifiers(CostModifiers.All),
                CardRef = c,
            };
            // 비용 수정자 캡처
            try
            {
                var ecObj = c.EnergyCost;
                var baseField = ecObj.GetType().GetField("_base", AllInstance);
                if (baseField != null)
                    cs.EnergyCostBase = (int)baseField.GetValue(ecObj)!;
                var modField = ecObj.GetType().GetField("_localModifiers", AllInstance);
                if (modField?.GetValue(ecObj) is System.Collections.IList mods && mods.Count > 0)
                {
                    cs.CostModifiers = new List<CostModifierSnapshot>();
                    foreach (var mod in mods)
                    {
                        var modType = mod.GetType();
                        cs.CostModifiers.Add(new CostModifierSnapshot
                        {
                            Amount = (int)(modType.GetProperty("Amount")?.GetValue(mod) ?? 0),
                            Type = (int)(modType.GetProperty("Type")?.GetValue(mod) ?? 0),
                            Expiration = (int)(modType.GetProperty("Expiration")?.GetValue(mod) ?? 0),
                            ReduceOnly = (bool)(modType.GetProperty("IsReduceOnly")?.GetValue(mod) ?? false),
                        });
                    }
                }
            }
            catch { }
            if (c.Affliction != null)
            {
                cs.AfflictionId = c.Affliction.Id.Entry;
                cs.AfflictionCategory = c.Affliction.Id.Category;
                cs.AfflictionAmount = c.Affliction.Amount;
            }
            if (c.Enchantment != null)
            {
                cs.EnchantmentId = c.Enchantment.Id.Entry;
                cs.EnchantmentCategory = c.Enchantment.Id.Category;
                cs.EnchantmentRef = c.Enchantment;  // 원본 참조 저장
                // _amount와 _status를 리플렉션으로 직접 읽어 값으로 저장 (참조가 변경되어도 보존)
                try
                {
                    var enchType = c.Enchantment.GetType();
                    var amountField = enchType.GetField("_amount", AllInstance);
                    if (amountField == null)
                    {
                        var bt = enchType;
                        while (bt != null && bt != typeof(object))
                        {
                            amountField = bt.GetField("_amount", AllInstance | BindingFlags.DeclaredOnly);
                            if (amountField != null) break;
                            bt = bt.BaseType;
                        }
                    }
                    cs.EnchantmentAmount = amountField != null ? (int)amountField.GetValue(c.Enchantment)! : c.Enchantment.Amount;

                    var statusField = enchType.GetField("_status", AllInstance);
                    if (statusField == null)
                    {
                        var bt = enchType;
                        while (bt != null && bt != typeof(object))
                        {
                            statusField = bt.GetField("_status", AllInstance | BindingFlags.DeclaredOnly);
                            if (statusField != null) break;
                            bt = bt.BaseType;
                        }
                    }
                    if (statusField != null)
                        cs.EnchantmentStatusVal = (int)Convert.ChangeType(statusField.GetValue(c.Enchantment)!, typeof(int));

                    // 인챈트 서브클래스 고유 필드 캡처 (기세 _extraDamage 등)
                    // EnchantmentModel 기본 필드(_amount, _status 등)는 위에서 이미 저장
                    // 서브클래스(Momentum 등)의 DeclaredOnly 필드를 추가로 캡처
                    var enchBaseType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Models.EnchantmentModel");
                    var subType = enchType;
                    while (subType != null && subType != enchBaseType && subType != typeof(object))
                    {
                        foreach (var f in subType.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                        {
                            try
                            {
                                var val = f.GetValue(c.Enchantment);
                                if (val is int || val is float || val is double || val is decimal || val is bool || val is string)
                                {
                                    cs.EnchantmentExtraFields[f.Name] = val;
                                }
                            }
                            catch { }
                        }
                        subType = subType.BaseType;
                    }
                    if (cs.EnchantmentExtraFields.Count > 0)
                        ModEntry.Log($"    인챈트 서브필드 캡처 ({cs.EnchantmentId}): {string.Join(", ", cs.EnchantmentExtraFields.Select(kv => $"{kv.Key}={kv.Value}"))}");

                }
                catch (Exception ex)
                {
                    cs.EnchantmentAmount = c.Enchantment.Amount;
                    ModEntry.Log($"    인챈트 필드 읽기 실패 ({cs.Id}): {ex.Message}");
                }
            }
            // DynamicVars 딥카피 (유전 알고리즘 등 누적 수치)
            // MemberwiseClone은 얕은 복사라서 _vars Dictionary를 공유함 → 딥카피 필요
            try
            {
                var dvField = typeof(CardModel).GetField("_dynamicVars", AllInstance);
                var dv = dvField?.GetValue(c);
                if (dv != null)
                {
                    // 1) DynamicVarSet를 MemberwiseClone
                    var cloneMethod = dv.GetType().GetMethod("MemberwiseClone",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var dvClone = cloneMethod?.Invoke(dv, null);
                    if (dvClone != null)
                    {
                        // 2) _vars Dictionary를 딥카피
                        var varsField = dv.GetType().GetField("_vars", AllInstance);
                        var origDict = varsField?.GetValue(dv) as System.Collections.IDictionary;
                        if (origDict != null && varsField != null)
                        {
                            // 새 Dictionary 생성 (같은 타입)
                            var newDict = Activator.CreateInstance(origDict.GetType()) as System.Collections.IDictionary;
                            if (newDict != null)
                            {
                                foreach (System.Collections.DictionaryEntry entry in origDict)
                                {
                                    // 각 DynamicVar도 MemberwiseClone
                                    object? clonedVal = entry.Value;
                                    if (clonedVal != null)
                                    {
                                        try
                                        {
                                            var valCloneMethod = clonedVal.GetType().GetMethod("MemberwiseClone",
                                                BindingFlags.Instance | BindingFlags.NonPublic);
                                            clonedVal = valCloneMethod?.Invoke(clonedVal, null) ?? clonedVal;
                                        }
                                        catch { }
                                    }
                                    newDict[entry.Key] = clonedVal;
                                }
                                varsField.SetValue(dvClone, newDict);
                            }
                        }
                        cs.DynamicVarsClone = dvClone;
                    }
                }
            }
            catch { }
            // CardModel 전체 계층 필드 캡처 (base 포함 — 유전 알고리즘 등 누적 수치)
            try
            {
                var cardType = c.GetType();
                var st = cardType;
                // CardModel 포함, object 직전까지 전부 캡처
                while (st != null && st != typeof(object))
                {
                    foreach (var f in st.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        try
                        {
                            // 이벤트/델리게이트/참조 타입은 제외, 값 타입만
                            var val = f.GetValue(c);
                            if (val is int || val is decimal || val is bool || val is float || val is double)
                            {
                                cs.SubclassFields ??= new();
                                cs.SubclassFields[f.Name] = val;
                            }
                        }
                        catch { }
                    }
                    st = st.BaseType;
                }
            }
            catch { }
            return cs;
        }).ToList();
    }

    /// <summary>카드 강화 상태를 스냅샷 시점으로 복원 (DowngradeInternal 사용)</summary>
    private static void RestoreCardUpgradeState(List<CardSnapshot> saved)
    {
        foreach (var cs in saved)
        {
            if (cs.CardRef == null) continue;
            if (cs.CardRef.IsUpgraded == cs.IsUpgraded) continue;

            try
            {
                if (cs.CardRef.IsUpgraded && !cs.IsUpgraded)
                {
                    var downgrade = cs.CardRef.GetType().GetMethod("DowngradeInternal", AllInstance);
                    if (downgrade != null)
                    {
                        downgrade.Invoke(cs.CardRef, null);
                        ModEntry.Log($"    카드 강화 해제: {cs.Id}");
                    }
                }
                else if (!cs.CardRef.IsUpgraded && cs.IsUpgraded)
                {
                    var upgrade = cs.CardRef.GetType().GetMethod("FinalizeUpgradeInternal", AllInstance);
                    if (upgrade != null)
                    {
                        upgrade.Invoke(cs.CardRef, null);
                        ModEntry.Log($"    카드 강화 복원: {cs.Id}");
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"    카드 강화 복원 실패 ({cs.Id}): {ex.InnerException?.Message ?? ex.Message}");
            }
        }
    }

    /// <summary>ChainsOfBindingPower의 boundCardPlayed 플래그를 false로 리셋</summary>
    private static void ResetBoundCardPlayedFlag(Creature creature)
    {
        try
        {
            var powers = creature.Powers;
            if (powers == null) return;
            foreach (var power in powers)
            {
                if (power.GetType().Name == "ChainsOfBindingPower")
                {
                    // _internalData 필드에 접근 → Data.boundCardPlayed = false
                    var internalDataField = typeof(PowerModel).GetField("_internalData", AllInstance);
                    if (internalDataField != null)
                    {
                        var data = internalDataField.GetValue(power);
                        if (data != null)
                        {
                            var boundField = data.GetType().GetField("boundCardPlayed", AllInstance | BindingFlags.Public);
                            if (boundField != null)
                            {
                                boundField.SetValue(data, false);
                                ModEntry.Log($"    ChainsOfBinding boundCardPlayed 리셋 완료");
                            }
                        }
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"    ChainsOfBinding 리셋 실패: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    /// <summary>ModelDb에서 AfflictionModel을 찾아 mutable 인스턴스 생성</summary>
    private static object? CreateMutableAffliction(string category, string entry)
    {
        try
        {
            var asm = typeof(CardModel).Assembly;
            var modelIdType = asm.GetType("MegaCrit.Sts2.Core.Models.ModelId");
            var modelDbType = asm.GetType("MegaCrit.Sts2.Core.Models.ModelDb");
            var afflictionType = asm.GetType("MegaCrit.Sts2.Core.Models.AfflictionModel");
            if (modelIdType == null || modelDbType == null || afflictionType == null) return null;

            // new ModelId(category, entry)
            var id = Activator.CreateInstance(modelIdType, new object[] { category, entry });

            // ModelDb.GetById<AfflictionModel>(id)
            var getById = modelDbType.GetMethod("GetById", BindingFlags.Public | BindingFlags.Static);
            var genericGetById = getById!.MakeGenericMethod(afflictionType);
            var canonical = genericGetById.Invoke(null, new[] { id });
            if (canonical == null) return null;

            // canonical.ToMutable()
            var toMutable = canonical.GetType().GetMethod("ToMutable", AllInstance);
            return toMutable?.Invoke(canonical, null);
        }
        catch (Exception ex)
        {
            ModEntry.Log($"    AfflictionModel 생성 실패 ({category}.{entry}): {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }

    /// <summary>카드 구속(affliction) 상태를 스냅샷 시점으로 복원</summary>
    private static void RestoreCardAfflictions(List<CardSnapshot> saved)
    {
        foreach (var cs in saved)
        {
            if (cs.CardRef == null) continue;

            try
            {
                var currentAffliction = cs.CardRef.Affliction;
                bool hadAffliction = cs.AfflictionId != null;
                bool hasAffliction = currentAffliction != null;

                if (hadAffliction && !hasAffliction)
                {
                    // 스냅샷에 구속이 있었는데 지금 없으면 → 복원
                    var mutable = CreateMutableAffliction(cs.AfflictionCategory!, cs.AfflictionId!);
                    if (mutable != null)
                    {
                        // NCard 이벤트 일시 제거 (ObjectDisposedException 방지)
                        SuppressCardEvents(cs.CardRef, () => {
                            var afflictInternal = cs.CardRef.GetType().GetMethod("AfflictInternal", AllInstance);
                            afflictInternal?.Invoke(cs.CardRef, new[] { mutable, (decimal)cs.AfflictionAmount });
                        });
                        ModEntry.Log($"    구속 복원: {cs.Id} ← {cs.AfflictionId}({cs.AfflictionAmount})");
                    }
                }
                else if (!hadAffliction && hasAffliction)
                {
                    // 스냅샷에 구속이 없었는데 지금 있으면 → 제거
                    SuppressCardEvents(cs.CardRef, () => {
                        var clearAffliction = cs.CardRef.GetType().GetMethod("ClearAfflictionInternal", AllInstance);
                        clearAffliction?.Invoke(cs.CardRef, null);
                    });
                    ModEntry.Log($"    구속 제거: {cs.Id} (was {currentAffliction!.Id.Entry})");
                }
                else if (hadAffliction && hasAffliction)
                {
                    // 둘 다 있지만 종류/수치가 다르면 → 교체
                    if (currentAffliction!.Id.Entry != cs.AfflictionId || currentAffliction.Amount != cs.AfflictionAmount)
                    {
                        SuppressCardEvents(cs.CardRef, () => {
                            var clearAffliction = cs.CardRef.GetType().GetMethod("ClearAfflictionInternal", AllInstance);
                            clearAffliction?.Invoke(cs.CardRef, null);
                        });

                        var mutable = CreateMutableAffliction(cs.AfflictionCategory!, cs.AfflictionId!);
                        if (mutable != null)
                        {
                            SuppressCardEvents(cs.CardRef, () => {
                                var afflictInternal = cs.CardRef.GetType().GetMethod("AfflictInternal", AllInstance);
                                afflictInternal?.Invoke(cs.CardRef, new[] { mutable, (decimal)cs.AfflictionAmount });
                            });
                            ModEntry.Log($"    구속 교체: {cs.Id} ← {cs.AfflictionId}({cs.AfflictionAmount})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"    구속 복원 실패 ({cs.Id}): {ex.InnerException?.Message ?? ex.Message}");
            }
        }
    }

    /// <summary>
    /// CardModel의 AfflictionChanged/EnchantmentChanged 이벤트를 일시적으로 제거한 상태에서 action 실행.
    /// NCard.OnAfflictionChanged() → ObjectDisposedException 방지.
    /// </summary>
    private static void SuppressCardEvents(CardModel card, Action action)
    {
        var afflField = FindField(card.GetType(), "AfflictionChanged");
        var enchField = FindField(card.GetType(), "EnchantmentChanged");

        var savedAffl = afflField?.GetValue(card);
        var savedEnch = enchField?.GetValue(card);

        afflField?.SetValue(card, null);
        enchField?.SetValue(card, null);

        try
        {
            action();
        }
        finally
        {
            afflField?.SetValue(card, savedAffl);
            enchField?.SetValue(card, savedEnch);
        }
    }

    /// <summary>ModelDb에서 EnchantmentModel을 찾아 mutable 인스턴스 생성</summary>
    private static object? CreateMutableEnchantment(string category, string entry)
    {
        try
        {
            var asm = typeof(CardModel).Assembly;
            var modelIdType = asm.GetType("MegaCrit.Sts2.Core.Models.ModelId");
            var modelDbType = asm.GetType("MegaCrit.Sts2.Core.Models.ModelDb");
            var enchantmentType = asm.GetType("MegaCrit.Sts2.Core.Models.EnchantmentModel");
            if (modelIdType == null || modelDbType == null || enchantmentType == null) return null;

            var id = Activator.CreateInstance(modelIdType, new object[] { category, entry });

            var getById = modelDbType.GetMethod("GetById", BindingFlags.Public | BindingFlags.Static);
            var genericGetById = getById!.MakeGenericMethod(enchantmentType);
            var canonical = genericGetById.Invoke(null, new[] { id });
            if (canonical == null) return null;

            var toMutable = canonical.GetType().GetMethod("ToMutable", AllInstance);
            return toMutable?.Invoke(canonical, null);
        }
        catch (Exception ex)
        {
            ModEntry.Log($"    EnchantmentModel 생성 실패 ({category}.{entry}): {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 카드에 새 mutable 인챈트를 생성하여 적용.
    /// ClearEnchantmentInternal → EnchantInternal → ModifyCard → FinalizeUpgradeInternal
    /// 게임의 정규 API 체인을 따라 효과와 비주얼 모두 복원.
    /// </summary>
    private static bool ApplyFreshEnchantment(CardModel card, string category, string entry, int amount)
    {
        try
        {
            // 1. 기존 인챈트 제거
            if (card.Enchantment != null)
            {
                var clearMethod = card.GetType().GetMethod("ClearEnchantmentInternal", AllInstance);
                clearMethod?.Invoke(card, null);
                ModEntry.Log($"      기존 인챈트 제거 완료");
            }

            // 2. 새 mutable 인챈트 생성
            var mutable = CreateMutableEnchantment(category, entry);
            if (mutable == null)
            {
                ModEntry.Log($"      인챈트 생성 실패: {category}.{entry}");
                return false;
            }

            // 3. EnchantInternal 호출 (AssertMutable → Enchantment 설정 → ApplyInternal → EnchantmentChanged 이벤트)
            var enchantInternal = card.GetType().GetMethod("EnchantInternal", AllInstance);
            if (enchantInternal != null)
            {
                enchantInternal.Invoke(card, new[] { mutable, (object)(decimal)amount });
                ModEntry.Log($"      EnchantInternal 성공: {entry}({amount})");
            }
            else
            {
                ModEntry.Log($"      EnchantInternal 메서드 없음");
                return false;
            }

            // 4. ModifyCard 호출 (OnEnchant → RecalculateValues → DynamicVars 재계산)
            var modifyCard = mutable.GetType().GetMethod("ModifyCard", AllInstance);
            if (modifyCard != null)
            {
                modifyCard.Invoke(mutable, null);
                ModEntry.Log($"      ModifyCard 성공");
            }

            // 5. FinalizeUpgradeInternal 호출
            var finalize = card.GetType().GetMethod("FinalizeUpgradeInternal", AllInstance);
            finalize?.Invoke(card, null);

            return true;
        }
        catch (Exception ex)
        {
            ModEntry.Log($"      인챈트 적용 실패: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    /// <summary>인챈트의 _amount, _status, 서브클래스 필드를 직접 강제 설정 (기세 등 누적형 인챈트 복원용)</summary>
    private static void ForceEnchantmentAmount(CardModel card, int amount, int statusVal, Dictionary<string, object?>? extraFields = null)
    {
        try
        {
            var ench = card.Enchantment;
            if (ench == null) return;

            var enchType = ench.GetType();

            // _amount 설정
            var amountField = FindFieldInHierarchy(enchType, "_amount");
            if (amountField != null)
            {
                var currentAmount = (int)amountField.GetValue(ench)!;
                if (currentAmount != amount)
                {
                    amountField.SetValue(ench, amount);
                    ModEntry.Log($"      인챈트 _amount 강제 설정: {currentAmount} → {amount}");
                }
            }

            // _status 설정
            var statusField = FindFieldInHierarchy(enchType, "_status");
            if (statusField != null)
            {
                var currentStatus = (int)Convert.ChangeType(statusField.GetValue(ench)!, typeof(int));
                if (currentStatus != statusVal)
                {
                    var enumVal = Enum.ToObject(statusField.FieldType, statusVal);
                    statusField.SetValue(ench, enumVal);
                    ModEntry.Log($"      인챈트 _status 강제 설정: {currentStatus} → {statusVal}");
                }
            }

            // 서브클래스 고유 필드 복원 (기세 _extraDamage 등)
            if (extraFields != null && extraFields.Count > 0)
            {
                foreach (var kvp in extraFields)
                {
                    var field = FindFieldInHierarchy(enchType, kvp.Key);
                    if (field != null && kvp.Value != null)
                    {
                        try
                        {
                            var current = field.GetValue(ench);
                            if (!kvp.Value.Equals(current))
                            {
                                field.SetValue(ench, Convert.ChangeType(kvp.Value, field.FieldType));
                                ModEntry.Log($"      인챈트 {kvp.Key} 강제 설정: {current} → {kvp.Value}");
                            }
                        }
                        catch (Exception ex)
                        {
                            ModEntry.Log($"      인챈트 {kvp.Key} 설정 실패: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"      인챈트 강제 설정 실패: {ex.Message}");
        }
    }

    private static FieldInfo? FindFieldInHierarchy(Type type, string fieldName)
    {
        var t = type;
        while (t != null && t != typeof(object))
        {
            var f = t.GetField(fieldName, AllInstance | BindingFlags.DeclaredOnly);
            if (f != null) return f;
            t = t.BaseType;
        }
        return null;
    }

    /// <summary>카드 인챈트(enchantment) 상태를 스냅샷 시점으로 복원</summary>
    private static void RestoreCardEnchantments(List<CardSnapshot> saved)
    {
        foreach (var cs in saved)
        {
            if (cs.CardRef == null) continue;

            try
            {
                var currentEnchantment = cs.CardRef.Enchantment;
                bool hadEnchantment = cs.EnchantmentId != null;
                bool hasEnchantment = currentEnchantment != null;

                ModEntry.Log($"    인챈트 체크: {cs.Id} had={hadEnchantment}({cs.EnchantmentId},{cs.EnchantmentAmount}) has={hasEnchantment}({currentEnchantment?.Id.Entry})");

                if (hadEnchantment && !hasEnchantment)
                {
                    // 스냅샷에 인챈트가 있었는데 지금 없으면 → 새로 생성하여 복원
                    SuppressCardEvents(cs.CardRef, () =>
                        ApplyFreshEnchantment(cs.CardRef, cs.EnchantmentCategory!, cs.EnchantmentId!, cs.EnchantmentAmount));
                    ForceEnchantmentAmount(cs.CardRef, cs.EnchantmentAmount, cs.EnchantmentStatusVal, cs.EnchantmentExtraFields);
                }
                else if (!hadEnchantment && hasEnchantment)
                {
                    // 스냅샷에 인챈트가 없었는데 지금 있으면 → 제거
                    SuppressCardEvents(cs.CardRef, () => {
                        var clearMethod = cs.CardRef.GetType().GetMethod("ClearEnchantmentInternal", AllInstance);
                        clearMethod?.Invoke(cs.CardRef, null);
                    });
                    ModEntry.Log($"    인챈트 제거: {cs.Id} (was {currentEnchantment!.Id.Entry})");
                }
                else if (hadEnchantment && hasEnchantment)
                {
                    // 같은 인챈트면 _amount, _status, 서브클래스 필드 직접 복원
                    if (currentEnchantment!.Id.Entry == cs.EnchantmentId)
                    {
                        ForceEnchantmentAmount(cs.CardRef, cs.EnchantmentAmount, cs.EnchantmentStatusVal, cs.EnchantmentExtraFields);
                    }
                    else
                    {
                        // 다른 인챈트면 새로 적용
                        SuppressCardEvents(cs.CardRef, () =>
                            ApplyFreshEnchantment(cs.CardRef, cs.EnchantmentCategory!, cs.EnchantmentId!, cs.EnchantmentAmount));
                        ForceEnchantmentAmount(cs.CardRef, cs.EnchantmentAmount, cs.EnchantmentStatusVal, cs.EnchantmentExtraFields);
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"    인챈트 복원 실패 ({cs.Id}): {ex.InnerException?.Message ?? ex.Message}");
            }
        }
    }

    /// <summary>구체(OrbQueue) 복원</summary>
    private void RestoreOrbs(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState pcs)
    {
        try
        {
            var orbQueue = pcs.OrbQueue;
            if (orbQueue == null) return;

            // 현재 상태와 비교
            var currentOrbs = orbQueue.Orbs;
            bool same = currentOrbs.Count == Orbs.Count && orbQueue.Capacity == OrbCapacity;
            if (same)
            {
                for (int i = 0; i < Orbs.Count; i++)
                {
                    if (currentOrbs[i] != Orbs[i].OrbRef)
                    {
                        same = false;
                        break;
                    }
                }
            }
            if (same) return;

            // Capacity 복원
            SetProperty(orbQueue, "Capacity", OrbCapacity);

            // _orbs 리스트 직접 교체
            var orbsField = orbQueue.GetType().GetField("_orbs", AllInstance);
            if (orbsField?.GetValue(orbQueue) is System.Collections.IList orbsList)
            {
                orbsList.Clear();
                foreach (var os in Orbs)
                {
                    if (os.OrbRef != null)
                        orbsList.Add(os.OrbRef);
                }
            }

            ModEntry.Log($"  구체 복원: {Orbs.Count}개, 용량 {OrbCapacity}");

            // NOrbManager UI 갱신
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree?.Root == null) return;
                var combatRoomNode = FindNodeByType(tree.Root, "NCombatRoom");
                if (combatRoomNode == null) return;

                // 플레이어 NCreature 노드에서 OrbManager 가져오기
                var player = pcs.GetType().GetProperty("Player", AllInstance)?.GetValue(pcs)
                    ?? pcs.GetType().GetField("_player", AllInstance)?.GetValue(pcs);
                if (player == null) return;

                var creatureProp = player.GetType().GetProperty("Creature", AllInstance);
                var creature = creatureProp?.GetValue(player) as Creature;
                if (creature == null) return;

                var getCreatureNode = combatRoomNode.GetType().GetMethod("GetCreatureNode", AllInstance);
                var nCreature = getCreatureNode?.Invoke(combatRoomNode, new object[] { creature }) as Godot.Node;
                if (nCreature == null) return;

                var orbManagerProp = nCreature.GetType().GetProperty("OrbManager", AllInstance);
                var orbManager = orbManagerProp?.GetValue(nCreature) as Godot.Node;
                if (orbManager == null) return;

                // NOrbManager._orbs (NOrb 노드 리스트)를 재구성
                var nOrbsField = orbManager.GetType().GetField("_orbs", AllInstance);
                var nOrbContainer = orbManager.GetType().GetField("_orbContainer", AllInstance)?.GetValue(orbManager) as Godot.Control;
                if (nOrbsField?.GetValue(orbManager) is System.Collections.IList nOrbsList && nOrbContainer != null)
                {
                    // 기존 NOrb 노드 모두 제거
                    foreach (var nOrb in nOrbsList)
                    {
                        if (nOrb is Godot.Node n && Godot.GodotObject.IsInstanceValid(n))
                            n.QueueFree();
                    }
                    nOrbsList.Clear();

                    // 새 NOrb 슬롯 생성 (Capacity 만큼)
                    var addSlotAnim = orbManager.GetType().GetMethod("AddSlotAnim", AllInstance);
                    addSlotAnim?.Invoke(orbManager, new object[] { OrbCapacity });

                    // 각 NOrb에 모델 설정
                    nOrbsList = nOrbsField.GetValue(orbManager) as System.Collections.IList;
                    if (nOrbsList != null)
                    {
                        for (int i = 0; i < Orbs.Count && i < nOrbsList.Count; i++)
                        {
                            if (Orbs[i].OrbRef != null && nOrbsList[i] is Godot.Node nOrbNode)
                            {
                                var replaceOrb = nOrbNode.GetType().GetMethod("ReplaceOrb", AllInstance);
                                replaceOrb?.Invoke(nOrbNode, new object[] { Orbs[i].OrbRef });
                            }
                        }
                    }

                    // UpdateVisuals 호출
                    var updateVisuals = orbManager.GetType().GetMethod("UpdateVisuals", AllInstance);
                    updateVisuals?.Invoke(orbManager, new object[] { 0 }); // OrbEvokeType.None = 0
                }

                ModEntry.Log($"  구체 UI 갱신 완료");
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  구체 UI 갱신 실패: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"  구체 복원 실패: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    /// <summary>카드 비용 수정자(_localModifiers)를 스냅샷 시점으로 복원</summary>
    private void RestoreCardCostModifiers(List<CardSnapshot> snapshots)
    {
        foreach (var cs in snapshots)
        {
            if (cs.CardRef == null) continue;
            try
            {
                var ecObj = cs.CardRef.EnergyCost;
                var baseField = ecObj.GetType().GetField("_base", AllInstance);
                var modField = ecObj.GetType().GetField("_localModifiers", AllInstance);
                if (baseField == null || modField == null) continue;

                bool changed = false;

                // _base 복원
                int currentBase = (int)baseField.GetValue(ecObj)!;
                if (currentBase != cs.EnergyCostBase)
                {
                    baseField.SetValue(ecObj, cs.EnergyCostBase);
                    ModEntry.Log($"    카드 비용 base 복원: {cs.Id} {currentBase} → {cs.EnergyCostBase}");
                    changed = true;
                }

                // _localModifiers 복원
                var currentMods = modField.GetValue(ecObj) as System.Collections.IList;
                if (cs.CostModifiers == null || cs.CostModifiers.Count == 0)
                {
                    // 스냅샷에 modifier 없었으면 현재 것도 클리어
                    if (currentMods != null && currentMods.Count > 0)
                    {
                        currentMods.Clear();
                        ModEntry.Log($"    카드 비용 modifier 클리어: {cs.Id}");
                        changed = true;
                    }
                }
                else
                {
                    // modifier 재구성
                    var localCostModType = typeof(CardEnergyCost).Assembly.GetType(
                        "MegaCrit.Sts2.Core.Entities.Cards.LocalCostModifier");
                    var localCostTypeEnum = typeof(CardEnergyCost).Assembly.GetType(
                        "MegaCrit.Sts2.Core.Entities.Cards.LocalCostType");
                    var localCostExpEnum = typeof(CardEnergyCost).Assembly.GetType(
                        "MegaCrit.Sts2.Core.Entities.Cards.LocalCostModifierExpiration");

                    if (localCostModType != null && localCostTypeEnum != null && localCostExpEnum != null && currentMods != null)
                    {
                        currentMods.Clear();
                        foreach (var cms in cs.CostModifiers)
                        {
                            var ctor = localCostModType.GetConstructors()[0];
                            var mod = ctor.Invoke(new object[]
                            {
                                cms.Amount,
                                Enum.ToObject(localCostTypeEnum, cms.Type),
                                Enum.ToObject(localCostExpEnum, cms.Expiration),
                                cms.ReduceOnly,
                            });
                            currentMods.Add(mod);
                        }
                        ModEntry.Log($"    카드 비용 modifier 복원: {cs.Id} ({cs.CostModifiers.Count}개)");
                        changed = true;
                    }
                }

                // 변경이 있으면 UI 갱신 이벤트 발화
                if (changed)
                    cs.CardRef.InvokeEnergyCostChanged();
            }
            catch (Exception ex)
            {
                ModEntry.Log($"    카드 비용 복원 실패 ({cs.Id}): {ex.InnerException?.Message ?? ex.Message}");
            }
        }
    }

    /// <summary>카드 DynamicVars + 서브클래스 필드 복원 (유전 알고리즘 등 누적 수치)</summary>
    private static void RestoreCardDynamicState(List<CardSnapshot> snapshots)
    {
        foreach (var cs in snapshots)
        {
            if (cs.CardRef == null) continue;

            // DynamicVars 딥카피 복원 (_dynamicVars 필드 직접 접근)
            try
            {
                var dvField = typeof(CardModel).GetField("_dynamicVars", AllInstance);
                if (dvField != null)
                {
                    var currentDv = dvField.GetValue(cs.CardRef);

                    if (cs.DynamicVarsClone != null)
                    {
                        // 스냅샷 보호를 위해 다시 딥카피
                        var cloneMethod = cs.DynamicVarsClone.GetType().GetMethod("MemberwiseClone",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        var freshClone = cloneMethod?.Invoke(cs.DynamicVarsClone, null);
                        if (freshClone != null)
                        {
                            // _vars Dictionary도 딥카피
                            var varsField = cs.DynamicVarsClone.GetType().GetField("_vars", AllInstance);
                            var origDict = varsField?.GetValue(cs.DynamicVarsClone) as System.Collections.IDictionary;
                            if (origDict != null && varsField != null)
                            {
                                var newDict = Activator.CreateInstance(origDict.GetType()) as System.Collections.IDictionary;
                                if (newDict != null)
                                {
                                    foreach (System.Collections.DictionaryEntry entry in origDict)
                                    {
                                        object? clonedVal = entry.Value;
                                        if (clonedVal != null)
                                        {
                                            try
                                            {
                                                var valClone = clonedVal.GetType().GetMethod("MemberwiseClone",
                                                    BindingFlags.Instance | BindingFlags.NonPublic);
                                                clonedVal = valClone?.Invoke(clonedVal, null) ?? clonedVal;
                                            }
                                            catch { }
                                        }
                                        newDict[entry.Key] = clonedVal;
                                    }
                                    varsField.SetValue(freshClone, newDict);
                                }
                            }
                            dvField.SetValue(cs.CardRef, freshClone);
                            ModEntry.Log($"    카드 DynamicVars 딥카피 복원: {cs.Id}");
                        }
                    }
                    else if (currentDv != null)
                    {
                        // 스냅샷 시점에 null이었으면 null로 되돌리기
                        dvField.SetValue(cs.CardRef, null);
                        ModEntry.Log($"    카드 DynamicVars null로 복원: {cs.Id}");
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"    카드 DynamicVars 복원 실패 ({cs.Id}): {ex.Message}");
            }

            // 서브클래스 고유 필드 복원
            if (cs.SubclassFields != null && cs.SubclassFields.Count > 0)
            {
                foreach (var kvp in cs.SubclassFields)
                {
                    try
                    {
                        var f = FindFieldInHierarchy(cs.CardRef.GetType(), kvp.Key);
                        if (f != null && kvp.Value != null)
                        {
                            var currentVal = f.GetValue(cs.CardRef);
                            if (!Equals(currentVal, kvp.Value))
                            {
                                f.SetValue(cs.CardRef, Convert.ChangeType(kvp.Value, f.FieldType));
                                ModEntry.Log($"    카드 서브필드 복원: {cs.Id}.{kvp.Key} = {currentVal} → {kvp.Value}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Log($"    카드 서브필드 복원 실패 ({cs.Id}.{kvp.Key}): {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>죽은 적을 부활시킴: NCreature 노드 재생성 + NCombatRoom에 추가</summary>
    private static void ReviveEnemy(Creature enemy)
    {
        // NCombatRoom.Instance에 접근
        var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
        if (tree?.Root == null) return;

        var combatRoomNode = FindNodeByType(tree.Root, "NCombatRoom");
        if (combatRoomNode == null)
        {
            ModEntry.Log("  NCombatRoom 노드를 찾을 수 없음");
            return;
        }

        var removingField = combatRoomNode.GetType().GetField("_removingCreatureNodes", AllInstance);
        var creatureNodesField = combatRoomNode.GetType().GetField("_creatureNodes", AllInstance);

        // 1) _removingCreatureNodes에서 이 적의 죽는 노드 정리
        if (removingField?.GetValue(combatRoomNode) is System.Collections.IList removingNodes)
        {
            for (int i = removingNodes.Count - 1; i >= 0; i--)
            {
                var node = removingNodes[i] as Godot.Node;
                if (node == null || !Godot.GodotObject.IsInstanceValid(node)) continue;

                var entityProp = node.GetType().GetProperty("Entity", AllInstance);
                var entity = entityProp?.GetValue(node) as Creature;
                if (entity == enemy)
                {
                    // 죽음 애니메이션 취소
                    try
                    {
                        var cancelTokenProp = node.GetType().GetProperty("DeathAnimCancelToken", AllInstance);
                        var cancelToken = cancelTokenProp?.GetValue(node) as System.Threading.CancellationTokenSource;
                        cancelToken?.Cancel();
                    }
                    catch { }

                    removingNodes.RemoveAt(i);

                    // 씬 트리에서 제거 (새 노드를 만들 것이므로)
                    try
                    {
                        if (node.GetParent() != null)
                            node.GetParent().RemoveChild(node);
                        node.QueueFree();
                    }
                    catch { }

                    ModEntry.Log($"  기존 죽는 노드 정리: {enemy.Name}");
                    break;
                }
            }
        }

        // 2) _creatureNodes에서도 중복 정리
        if (creatureNodesField?.GetValue(combatRoomNode) is System.Collections.IList existingNodes)
        {
            for (int i = existingNodes.Count - 1; i >= 0; i--)
            {
                var node = existingNodes[i] as Godot.Node;
                if (node == null || !Godot.GodotObject.IsInstanceValid(node)) continue;

                var entityProp = node.GetType().GetProperty("Entity", AllInstance);
                var entity = entityProp?.GetValue(node) as Creature;
                if (entity == enemy)
                {
                    existingNodes.RemoveAt(i);
                    try
                    {
                        if (node.GetParent() != null)
                            node.GetParent().RemoveChild(node);
                        node.QueueFree();
                    }
                    catch { }
                    ModEntry.Log($"  기존 노드 정리: {enemy.Name}");
                }
            }
        }

        // 3) CombatState에 적 다시 등록 (죽을 때 RemoveCreature로 제거됨)
        var combatState = CombatManager.Instance?.DebugOnlyGetState();
        if (combatState != null)
        {
            // CombatState가 null이면 복원 (RemoveCreature에서 null로 설정됨)
            if (enemy.CombatState == null)
            {
                SetProperty(enemy, "CombatState", combatState);
                ModEntry.Log($"  CombatState 복원: {enemy.Name}");
            }

            // _enemies 리스트에 다시 추가
            if (!combatState.ContainsCreature(enemy))
            {
                try
                {
                    combatState.AddCreature(enemy);
                    ModEntry.Log($"  CombatState.Enemies에 재등록: {enemy.Name}");
                }
                catch (Exception ex)
                {
                    ModEntry.Log($"  CombatState.AddCreature 실패: {ex.Message}");
                    // 실패 시 직접 _enemies 리스트에 추가
                    try
                    {
                        var enemiesField = combatState.GetType().GetField("_enemies", AllInstance);
                        if (enemiesField?.GetValue(combatState) is System.Collections.IList enemiesList)
                        {
                            enemiesList.Add(enemy);
                            ModEntry.Log($"  _enemies 리스트에 직접 추가: {enemy.Name}");
                        }
                    }
                    catch { }
                }
            }
        }

        // 4) NCombatRoom.AddCreature()로 새 UI 노드 생성
        //    → NCreature.Create() → _Ready() → _stateDisplay.SetCreature(Entity)
        //    → NPowerContainer.SetCreature() → 현재 creature.Powers를 읽어 NPower 노드 생성
        //    → NHealthBar.SetCreature() → HP 표시
        var addCreature = combatRoomNode.GetType().GetMethod("AddCreature", AllInstance);
        if (addCreature != null)
        {
            addCreature.Invoke(combatRoomNode, new object[] { enemy });
            ModEntry.Log($"  적 부활 (새 노드 생성): {enemy.Name}");

            // 4) 인텐트 UI 갱신
            try
            {
                var getCreatureNode = combatRoomNode.GetType().GetMethod("GetCreatureNode", AllInstance);
                var newNode = getCreatureNode?.Invoke(combatRoomNode, new object[] { enemy }) as Godot.Node;
                if (newNode != null)
                {
                    var refreshIntents = newNode.GetType().GetMethod("RefreshIntents", AllInstance);
                    refreshIntents?.Invoke(newNode, null);
                    ModEntry.Log($"  인텐트 UI 갱신 완료");
                }
            }
            catch (Exception ex) { ModEntry.Log($"  인텐트 UI 갱신 실패: {ex.Message}"); }
        }
        else
        {
            ModEntry.Log($"  AddCreature 메서드를 찾을 수 없음");
        }
    }

    private static Godot.Node? _pendingHandRefresh;

    private static void OnDeferredHandRefresh()
    {
        // 1회만 실행, 시그널 해제
        var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
        if (tree != null)
            tree.ProcessFrame -= OnDeferredHandRefresh;

        var handNode = _pendingHandRefresh;
        _pendingHandRefresh = null;
        if (handNode == null) return;

        try
        {
            var containerProp = handNode.GetType().GetProperty("CardHolderContainer", AllInstance);
            var container = containerProp?.GetValue(handNode) as Godot.Control;
            if (container == null)
            {
                ModEntry.Log("  지연 갱신: CardHolderContainer 없음");
                return;
            }

            int updated = 0;
            for (int i = 0; i < container.GetChildCount(); i++)
            {
                var child = container.GetChild(i);
                // UpdateCard 호출 시도
                var updateCard = child.GetType().GetMethod("UpdateCard",
                    BindingFlags.Instance | BindingFlags.Public);
                if (updateCard != null)
                {
                    try
                    {
                        updateCard.Invoke(child, null);
                        updated++;
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Log($"  UpdateCard 실패: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }

                // NHandCardHolder → CardNode(NCard) → 인챈트 비주얼 갱신
                try
                {
                    var cardNodeProp = child.GetType().GetProperty("CardNode", AllInstance);
                    var cardNode = cardNodeProp?.GetValue(child);
                    if (cardNode != null)
                    {
                        var cmProp = child.GetType().GetProperty("CardModel", AllInstance);
                        var cm = cmProp?.GetValue(child) as CardModel;
                        if (cm?.Enchantment != null)
                        {
                            // 먼저 UpdateEnchantmentVisuals 시도 (이미 구독돼있을 수 있음)
                            var updateEnchVis = cardNode.GetType().GetMethod("UpdateEnchantmentVisuals", AllInstance);
                            updateEnchVis?.Invoke(cardNode, null);

                            // EnchantmentTab 비주얼을 직접 Visible로 설정
                            var enchTabProp = cardNode.GetType().GetProperty("EnchantmentTab", AllInstance);
                            var enchTab = enchTabProp?.GetValue(cardNode) as Godot.Control;
                            if (enchTab != null)
                            {
                                enchTab.Visible = true;
                            }
                        }
                    }
                }
                catch (Exception enchEx)
                {
                    ModEntry.Log($"  인챈트 비주얼 갱신 실패: {enchEx.InnerException?.Message ?? enchEx.Message}");
                }
            }
            ModEntry.Log($"  지연 핸드 비주얼 갱신: {updated}장");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"  지연 핸드 비주얼 갱신 실패: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static bool _combatStateLogged;
    private static bool _playerLogged;
    private static bool _pileButtonLogged;

    /// <summary>카드 더미 버튼 UI + 카드 플레이 상태 갱신</summary>
    private void RefreshPileButtons(PlayerCombatState pcs, int preDrawCount, int preDiscardCount, int preExhaustCount)
    {
        try
        {
            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            if (tree?.Root == null) return;

            // 1. 카드 더미 버튼 카운트 갱신
            SetPileButtonCount(tree.Root, "NDrawPileButton", pcs.DrawPile.Cards.Count, preDrawCount, "Draw");
            SetPileButtonCount(tree.Root, "NDiscardPileButton", pcs.DiscardPile.Cards.Count, preDiscardCount, "Discard");
            SetPileButtonCount(tree.Root, "NExhaustPileButton", pcs.ExhaustPile.Cards.Count, preExhaustCount, "Exhaust");

            // 2. 카드 플레이 상태 초기화 (카드 멈춤 방지) + 핸드 카드 비주얼 갱신
            var handNode = FindNodeByType(tree.Root, "NPlayerHand");
            if (handNode != null)
            {
                var cancelMethod = handNode.GetType().GetMethod("CancelAllCardPlay", AllInstance);
                if (cancelMethod != null)
                {
                    cancelMethod.Invoke(handNode, null);
                    ModEntry.Log("  CancelAllCardPlay 호출");
                }

                // 핸드/턴 종료 버튼 상태 복원 (별도 try-catch로 감싸서 실패해도 핸드 갱신에 영향 없도록)
                try
                {
                    // _currentCardPlay를 null로 직접 세팅 (InCardPlay 해제)
                    var cardPlayField = handNode.GetType().GetField("_currentCardPlay", AllInstance);
                    if (cardPlayField != null)
                    {
                        cardPlayField.SetValue(handNode, null);
                        ModEntry.Log("  _currentCardPlay = null");
                    }

                    // CurrentMode를 Play로 리셋
                    var modeField = handNode.GetType().GetField("_currentMode", AllInstance);
                    if (modeField != null)
                    {
                        var modeEnumVal = Enum.ToObject(modeField.FieldType, 1); // Mode.Play = 1
                        modeField.SetValue(handNode, modeEnumVal);
                        ModEntry.Log("  _currentMode = Play");
                    }

                    // _holdersAwaitingQueue 클리어 (카드 플레이 대기열)
                    var awaitField = handNode.GetType().GetField("_holdersAwaitingQueue", AllInstance);
                    if (awaitField != null)
                    {
                        var awaitDict = awaitField.GetValue(handNode);
                        if (awaitDict != null)
                        {
                            var clearAwait = awaitDict.GetType().GetMethod("Clear");
                            clearAwait?.Invoke(awaitDict, null);
                            ModEntry.Log("  _holdersAwaitingQueue 클리어");
                        }
                    }

                    // NEndTurnButton 상태 복원 (턴 종료 가능하도록)
                    var endTurnBtn = FindNodeByType(tree.Root, "NEndTurnButton");
                    if (endTurnBtn != null)
                    {
                        // _state를 Enabled(0)으로 세팅 + Hidden이었으면 AnimIn 호출
                        var stateField = endTurnBtn.GetType().GetField("_state", AllInstance);
                        if (stateField != null)
                        {
                            var oldState = (int)Convert.ChangeType(stateField.GetValue(endTurnBtn)!, typeof(int));
                            var stateEnumVal = Enum.ToObject(stateField.FieldType, 0); // State.Enabled = 0
                            stateField.SetValue(endTurnBtn, stateEnumVal);
                            ModEntry.Log($"  NEndTurnButton._state = Enabled (was {oldState})");

                            // Hidden(2)이었으면 AnimIn으로 버튼을 화면에 복귀
                            if (oldState == 2) // State.Hidden
                            {
                                var animIn = endTurnBtn.GetType().GetMethod("AnimIn", AllInstance);
                                if (animIn != null)
                                {
                                    animIn.Invoke(endTurnBtn, null);
                                    ModEntry.Log("  NEndTurnButton.AnimIn() 호출 (Hidden → Enabled)");
                                }
                                else
                                {
                                    // AnimIn 못 찾으면 ShowPos로 직접 이동
                                    var showPosProp = endTurnBtn.GetType().GetProperty("ShowPos", AllInstance);
                                    if (showPosProp == null)
                                    {
                                        var bt = endTurnBtn.GetType();
                                        while (bt != null && bt != typeof(Godot.Node))
                                        {
                                            showPosProp = bt.GetProperty("ShowPos", AllInstance | BindingFlags.DeclaredOnly);
                                            if (showPosProp != null) break;
                                            bt = bt.BaseType;
                                        }
                                    }
                                    if (showPosProp != null)
                                    {
                                        var showPos = showPosProp.GetValue(endTurnBtn);
                                        if (showPos != null)
                                        {
                                            var posProp = endTurnBtn.GetType().GetProperty("Position");
                                            posProp?.SetValue(endTurnBtn, showPos);
                                            ModEntry.Log($"  NEndTurnButton.Position = ShowPos (폴백)");
                                        }
                                    }
                                }
                            }
                        }

                        // 진단: RefreshEnabled 조건 로깅
                        try
                        {
                            var combatRoomType = typeof(MegaCrit.Sts2.Core.Combat.CombatManager).Assembly
                                .GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
                            var roomInstance = combatRoomType?.GetProperty("Instance",
                                BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                            if (roomInstance != null)
                            {
                                var roomMode = roomInstance.GetType().GetProperty("Mode", AllInstance)?.GetValue(roomInstance);
                                ModEntry.Log($"  진단: NCombatRoom.Mode = {roomMode}");

                                // ActiveScreenContext.Instance.IsCurrent(room)
                                var ascType = typeof(MegaCrit.Sts2.Core.Combat.CombatManager).Assembly
                                    .GetType("MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext");
                                var ascInstance = ascType?.GetProperty("Instance",
                                    BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                                if (ascInstance != null)
                                {
                                    var isCurrentMethod = ascInstance.GetType().GetMethod("IsCurrent", AllInstance);
                                    if (isCurrentMethod != null)
                                    {
                                        var isCurrent = isCurrentMethod.Invoke(ascInstance, new object[] { roomInstance });
                                        ModEntry.Log($"  진단: ActiveScreenContext.IsCurrent(NCombatRoom) = {isCurrent}");
                                    }
                                }

                                // Hand.IsInCardSelection
                                var uiProp = roomInstance.GetType().GetProperty("Ui", AllInstance);
                                var ui = uiProp?.GetValue(roomInstance);
                                var handProp = ui?.GetType().GetProperty("Hand", AllInstance);
                                var handRef = handProp?.GetValue(ui);
                                if (handRef != null)
                                {
                                    var inCardSelProp = handRef.GetType().GetProperty("IsInCardSelection", AllInstance);
                                    var inCardSel = inCardSelProp?.GetValue(handRef);
                                    ModEntry.Log($"  진단: Hand.IsInCardSelection = {inCardSel}");

                                    var inCardPlayProp = handRef.GetType().GetProperty("InCardPlay", AllInstance);
                                    var inCardPlay = inCardPlayProp?.GetValue(handRef);
                                    ModEntry.Log($"  진단: Hand.InCardPlay = {inCardPlay}");

                                    var curModeProp = handRef.GetType().GetProperty("CurrentMode", AllInstance);
                                    var curMode = curModeProp?.GetValue(handRef);
                                    ModEntry.Log($"  진단: Hand.CurrentMode = {curMode}");
                                }
                            }

                            // NButton._isEnabled 확인
                            var isEnabledField = endTurnBtn.GetType().GetField("_isEnabled", AllInstance);
                            if (isEnabledField == null)
                            {
                                var bt = endTurnBtn.GetType().BaseType;
                                while (bt != null && bt != typeof(Godot.Node))
                                {
                                    isEnabledField = bt.GetField("_isEnabled", AllInstance | BindingFlags.DeclaredOnly);
                                    if (isEnabledField != null) break;
                                    bt = bt.BaseType;
                                }
                            }
                            ModEntry.Log($"  진단: NEndTurnButton._isEnabled = {isEnabledField?.GetValue(endTurnBtn)}");
                        }
                        catch (Exception diag)
                        {
                            ModEntry.Log($"  진단 실패: {diag.Message}");
                        }

                        // RefreshEnabled 호출하여 Enable/Disable 동기화
                        var refreshMethod = endTurnBtn.GetType().GetMethod("RefreshEnabled", AllInstance);
                        refreshMethod?.Invoke(endTurnBtn, null);

                        // 턴 번호 레이블 갱신: OnTurnStarted 호출하여 게임 내부 로직으로 텍스트 갱신
                        try
                        {
                            var onTurnStarted = endTurnBtn.GetType().GetMethod("OnTurnStarted", AllInstance);
                            if (onTurnStarted != null)
                            {
                                var combatState = CombatManager.Instance?.DebugOnlyGetState();
                                if (combatState != null)
                                {
                                    onTurnStarted.Invoke(endTurnBtn, new object[] { combatState });
                                    ModEntry.Log($"  턴종료 버튼 텍스트 갱신: OnTurnStarted(턴 {RoundNumber})");
                                }
                            }
                            else
                            {
                                // 폴백: 직접 _label.SetTextAutoSize
                                var labelField = endTurnBtn.GetType().GetField("_label", AllInstance);
                                var label = labelField?.GetValue(endTurnBtn);
                                if (label != null)
                                {
                                    var setTextMethod = label.GetType().GetMethod("SetTextAutoSize");
                                    setTextMethod?.Invoke(label, new object[] { $"{RoundNumber}턴 종료" });
                                    ModEntry.Log($"  턴종료 버튼 텍스트 갱신 (폴백): {RoundNumber}턴 종료");
                                }
                            }
                        }
                        catch (Exception labelEx)
                        {
                            ModEntry.Log($"  턴종료 버튼 텍스트 갱신 실패: {labelEx.Message}");
                        }

                        // RefreshEnabled 호출 후 _isEnabled 재확인
                        try
                        {
                            var isEnabledField2 = endTurnBtn.GetType().GetField("_isEnabled", AllInstance);
                            if (isEnabledField2 == null)
                            {
                                var bt2 = endTurnBtn.GetType().BaseType;
                                while (bt2 != null && bt2 != typeof(Godot.Node))
                                {
                                    isEnabledField2 = bt2.GetField("_isEnabled", AllInstance | BindingFlags.DeclaredOnly);
                                    if (isEnabledField2 != null) break;
                                    bt2 = bt2.BaseType;
                                }
                            }
                            ModEntry.Log($"  NEndTurnButton RefreshEnabled 후 _isEnabled = {isEnabledField2?.GetValue(endTurnBtn)}");
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.Log($"  턴종료 상태 복원 실패 (무시): {ex.Message}");
                }

                // 핸드 카드 비주얼 갱신을 다음 프레임으로 지연 (현재 프레임에선 노드가 준비 안 됨)
                _pendingHandRefresh = handNode;
                var refreshTree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (refreshTree != null)
                {
                    refreshTree.ProcessFrame += OnDeferredHandRefresh;
                    ModEntry.Log("  핸드 카드 비주얼 갱신 예약됨 (다음 프레임)");
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"UI 갱신 실패: {ex.Message}");
        }
    }

    private void SetPileButtonCount(Godot.Node root, string btnTypeName, int targetCount, int preCount, string name)
    {
        var btn = FindNodeByType(root, btnTypeName);
        if (btn == null) return;

        // 방법 1: _currentCount 필드 직접 설정 (NCombatCardPile 기본 클래스)
        FieldInfo? countField = null;
        var searchType = btn.GetType();
        while (searchType != null && searchType != typeof(Godot.Node))
        {
            countField = searchType.GetField("_currentCount", AllInstance | BindingFlags.DeclaredOnly);
            if (countField != null) break;
            searchType = searchType.BaseType;
        }

        if (countField != null && countField.FieldType == typeof(int))
        {
            countField.SetValue(btn, targetCount);
            ModEntry.Log($"  {name} _currentCount 설정: {targetCount}");
        }

        // 방법 2: _countLabel.SetTextAutoSize 호출로 라벨 텍스트 동기화
        try
        {
            var labelField = btn.GetType().GetField("_countLabel", AllInstance);
            if (labelField == null)
            {
                searchType = btn.GetType().BaseType;
                while (searchType != null && searchType != typeof(Godot.Node))
                {
                    labelField = searchType.GetField("_countLabel", AllInstance | BindingFlags.DeclaredOnly);
                    if (labelField != null) break;
                    searchType = searchType.BaseType;
                }
            }
            if (labelField != null)
            {
                var labelObj = labelField.GetValue(btn);
                if (labelObj != null)
                {
                    var setTextMethod = labelObj.GetType().GetMethod("SetTextAutoSize", AllInstance);
                    setTextMethod?.Invoke(labelObj, new object[] { targetCount.ToString() });
                    ModEntry.Log($"  {name} _countLabel.SetTextAutoSize({targetCount})");
                }
            }
        }
        catch { }

        // 방법 3 (폴백): Label 자식 찾아서 텍스트 직접 설정
        SetLabelRecursive(btn, targetCount.ToString(), name);
    }

    private void SetLabelRecursive(Godot.Node node, string text, string name)
    {
        if (node is Godot.Label label)
        {
            // 숫자가 포함된 Label만 업데이트
            if (int.TryParse(label.Text, out _))
            {
                label.Text = text;
                ModEntry.Log($"  {name} Label 텍스트 설정: {text}");
                return;
            }
        }
        for (int i = 0; i < node.GetChildCount(); i++)
            SetLabelRecursive(node.GetChild(i), text, name);
    }

    private static bool _powerLogged;

    /// <summary>파워(버프/디버프) 복원</summary>
    private void RestorePowers(Creature creature, List<PowerSnapshot> savedPowers)
    {
        try
        {
            var powers = creature.Powers;

            // 현재 파워 수량 조정: 저장된 파워와 비교
            var savedPowerMap = new Dictionary<string, PowerSnapshot>();
            foreach (var sp in savedPowers)
                savedPowerMap[sp.Id] = sp;

            // 저장 상태에 없는 파워 → RemoveInternal()로 완전 제거
            // (리스트에서 제거 + PowerRemoved 이벤트 → NPower UI 자동 정리)
            var powersToRemove = new List<PowerModel>();
            foreach (var power in powers)
            {
                var id = power.Id.Entry;
                if (savedPowerMap.TryGetValue(id, out var savedSnap))
                {
                    if (power.Amount != savedSnap.Amount)
                    {
                        int beforeAmount = power.Amount;
                        bool amountSet = false;

                        // 1) _amount 필드 직접 설정
                        foreach (var fname in new[] { "_amount", "_Amount", "amount" })
                        {
                            var af = FindFieldInHierarchy(power.GetType(), fname);
                            if (af != null)
                            {
                                af.SetValue(power, savedSnap.Amount);
                                amountSet = true;
                                ModEntry.Log($"    파워 Amount 필드 '{fname}' 설정: {id} {beforeAmount} → {savedSnap.Amount} (실제: {power.Amount})");
                                break;
                            }
                        }

                        // 2) 필드 못 찾으면 프로퍼티 setter 시도
                        if (!amountSet || power.Amount != savedSnap.Amount)
                        {
                            try
                            {
                                SetProperty(power, "Amount", savedSnap.Amount);
                                ModEntry.Log($"    파워 Amount 프로퍼티 설정: {id} → {savedSnap.Amount} (실제: {power.Amount})");
                            }
                            catch { }
                        }

                        // 3) backing field 패턴 시도 (<Amount>k__BackingField)
                        if (power.Amount != savedSnap.Amount)
                        {
                            var backingField = FindFieldInHierarchy(power.GetType(), "<Amount>k__BackingField");
                            if (backingField != null)
                            {
                                backingField.SetValue(power, savedSnap.Amount);
                                ModEntry.Log($"    파워 Amount backing field 설정: {id} → {savedSnap.Amount} (실제: {power.Amount})");
                            }
                        }

                        // AmountChanged 이벤트 발화 → NPower UI 갱신
                        try
                        {
                            var amountChangedField = FindField(power.GetType(), "AmountChanged");
                            var handler = amountChangedField?.GetValue(power) as Action;
                            handler?.Invoke();
                        }
                        catch { }

                        ModEntry.Log($"    파워 수량 복원 결과: {id} {beforeAmount} → {savedSnap.Amount} (최종: {power.Amount})");
                    }
                    // _internalData 내부 필드 복원 (자동화 cardsLeft 등)
                    RestorePowerInternalData(power, savedSnap);
                    savedPowerMap.Remove(id);
                }
                else
                {
                    powersToRemove.Add(power);
                }
            }

            // 게임의 정식 제거 흐름 사용: PowerModel.RemoveInternal()
            // → Removed 이벤트 → Creature.RemovePowerInternal() → PowerRemoved 이벤트
            // → NPowerContainer가 NPower 노드 정리 + NCreature가 이벤트 구독 해제
            foreach (var power in powersToRemove)
            {
                try
                {
                    var removeInternal = power.GetType().GetMethod("RemoveInternal", AllInstance);
                    if (removeInternal != null)
                    {
                        removeInternal.Invoke(power, null);
                        ModEntry.Log($"    파워 제거: {power.Id.Entry}");
                    }
                    else
                    {
                        ModEntry.Log($"    RemoveInternal 메서드 없음: {power.Id.Entry}");
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.Log($"    파워 제거 실패 ({power.Id.Entry}): {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            // 스냅샷에 있지만 현재 없는 파워 → 게임 API로 새 인스턴스 생성하여 적용
            // RemoveInternal() 된 PowerRef는 이벤트 구독이 끊어져 재사용 불가
            if (savedPowerMap.Count > 0)
            {
                foreach (var kvp in savedPowerMap)
                {
                    string powerId = kvp.Key;
                    var savedSnap = kvp.Value;
                    try
                    {
                        if (savedSnap.PowerRef == null)
                        {
                            ModEntry.Log($"    파워 추가 실패: {powerId} - PowerRef 없음");
                            continue;
                        }

                        // 이미 creature에 같은 파워가 존재하면 amount만 보정 (중복 추가 방지)
                        var existingPower = creature.Powers.FirstOrDefault(p => p.Id.Entry == powerId);
                        if (existingPower != null)
                        {
                            if (existingPower.Amount != savedSnap.Amount)
                            {
                                var af = FindFieldInHierarchy(existingPower.GetType(), "_amount");
                                af?.SetValue(existingPower, savedSnap.Amount);
                                ModEntry.Log($"    파워 이미 존재, amount 보정: {powerId} → {savedSnap.Amount}");
                            }
                            RestorePowerInternalData(existingPower, savedSnap);
                            continue;
                        }

                        // ModelDb에서 새 mutable 파워 인스턴스 생성
                        var newPower = CreateFreshPower(savedSnap.PowerRef.Id.Category, savedSnap.PowerRef.Id.Entry, savedSnap.PowerRef);
                        if (newPower != null)
                        {
                            // PowerModel.ApplyInternal(Creature owner) 호출
                            // → 내부에서 Creature.ApplyPowerInternal 호출 + 이벤트 구독 + UI 생성
                            var amountField = FindFieldInHierarchy(newPower.GetType(), "_amount");
                            amountField?.SetValue(newPower, savedSnap.Amount);

                            bool applied = false;
                            var applyInternalMethods = newPower.GetType().GetMethods(AllInstance)
                                .Where(m => m.Name == "ApplyInternal").ToArray();

                            foreach (var aim in applyInternalMethods)
                            {
                                var parms = aim.GetParameters();
                                ModEntry.Log($"    PowerModel.ApplyInternal: ({string.Join(", ", parms.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                                try
                                {
                                    if (parms.Length == 3 &&
                                        typeof(Creature).IsAssignableFrom(parms[0].ParameterType) &&
                                        parms[1].ParameterType == typeof(decimal) &&
                                        parms[2].ParameterType == typeof(bool))
                                    {
                                        aim.Invoke(newPower, new object[] { creature, (decimal)savedSnap.Amount, true });
                                        applied = true;
                                    }
                                    else if (parms.Length == 1 && typeof(Creature).IsAssignableFrom(parms[0].ParameterType))
                                    {
                                        aim.Invoke(newPower, new object[] { creature });
                                        applied = true;
                                    }
                                    else if (parms.Length == 0)
                                    {
                                        SetProperty(newPower, "Owner", creature);
                                        aim.Invoke(newPower, null);
                                        applied = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    ModEntry.Log($"    ApplyInternal 호출 실패: {ex.InnerException?.Message ?? ex.Message}");
                                }
                                if (applied) break;
                            }

                            if (applied)
                            {
                                ModEntry.Log($"    파워 ApplyInternal 성공: {powerId} = {savedSnap.Amount}");
                                RestorePowerInternalData(newPower, savedSnap);

                                var actualAmount = newPower.Amount;
                                if (actualAmount != savedSnap.Amount)
                                {
                                    var af = FindFieldInHierarchy(newPower.GetType(), "_amount");
                                    af?.SetValue(newPower, savedSnap.Amount);
                                    ModEntry.Log($"    파워 _amount 보정: {actualAmount} → {savedSnap.Amount}");
                                }
                            }
                            else
                            {
                                ModEntry.Log($"    ApplyInternal 실패, fallback 사용");
                                FallbackAddPower(creature, savedSnap, powerId);
                            }
                        }
                        else
                        {
                            ModEntry.Log($"    파워 생성 실패, fallback 사용: {powerId}");
                            FallbackAddPower(creature, savedSnap, powerId);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Log($"    파워 추가 실패 ({powerId}): {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"파워 복원 실패: {ex.Message}");
        }
    }

    /// <summary>PowerRef의 canonical 인스턴스에서 새 mutable 파워 생성</summary>
    private static PowerModel? CreateFreshPower(string category, string entry, PowerModel? originalRef = null)
    {
        // ToMutable(int initialAmount) — canonical 인스턴스에서 호출해야 함
        if (originalRef != null)
        {
            try
            {
                // canonical 인스턴스 찾기
                object? canonical = null;
                var canonField = FindFieldInHierarchy(originalRef.GetType(), "_canonicalInstance");
                if (canonField != null)
                    canonical = canonField.GetValue(originalRef);
                if (canonical == null)
                {
                    var canonProp = originalRef.GetType().GetProperty("CanonicalInstance", AllInstance);
                    canonical = canonProp?.GetValue(originalRef);
                }

                if (canonical != null)
                {
                    // ToMutable(int initialAmount) 호출
                    var toMutable = canonical.GetType().GetMethod("ToMutable", AllInstance,
                        null, new[] { typeof(int) }, null);
                    if (toMutable != null)
                    {
                        var result = toMutable.Invoke(canonical, new object[] { 0 }) as PowerModel;
                        if (result != null)
                        {
                            ModEntry.Log($"    파워 생성 성공 (canonical.ToMutable(0)): {entry}");
                            return result;
                        }
                    }
                }
                else
                {
                    ModEntry.Log($"    canonical 인스턴스 없음: {entry}");
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"    canonical.ToMutable 실패: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        ModEntry.Log($"    파워 생성 실패: {category}.{entry} - 모든 방법 실패");
        return null;
    }

    /// <summary>ApplyPowerInternal이 없을 때의 fallback: _powers 직접 추가 + 이벤트</summary>
    private static void FallbackAddPower(Creature creature, PowerSnapshot savedSnap, string powerId)
    {
        if (savedSnap.PowerRef == null) return;

        var powersField = creature.GetType().GetField("_powers", AllInstance);
        var powersList = powersField?.GetValue(creature) as System.Collections.IList;

        SetProperty(savedSnap.PowerRef, "Owner", creature);
        var amountField = FindFieldInHierarchy(savedSnap.PowerRef.GetType(), "_amount");
        amountField?.SetValue(savedSnap.PowerRef, savedSnap.Amount);
        RestorePowerInternalData(savedSnap.PowerRef, savedSnap);

        if (powersList != null && !powersList.Contains(savedSnap.PowerRef))
        {
            powersList.Add(savedSnap.PowerRef);
            try
            {
                var paField = creature.GetType().GetField("PowerApplied", AllInstance);
                var handler = paField?.GetValue(creature) as Action<PowerModel>;
                handler?.Invoke(savedSnap.PowerRef);
            }
            catch { }
            ModEntry.Log($"    파워 fallback 추가: {powerId} = {savedSnap.Amount}");
        }
    }

    private static bool _cardPileLogged;
    private static bool _pcsLogged;

    private void RestoreCardPile(CardPile pile, List<CardSnapshot> saved)
    {
        try
        {
            // 첫 호출 시 CardPile 구조 로그
            if (!_cardPileLogged)
            {
                _cardPileLogged = true;
                ModEntry.Log($"=== CardPile 타입: {pile.GetType().FullName} ===");
                foreach (var f in pile.GetType().GetFields(AllInstance))
                    ModEntry.Log($"  F: {f.Name} : {f.FieldType.Name}");
                foreach (var p in pile.GetType().GetProperties(AllInstance))
                    ModEntry.Log($"  P: {p.Name} : {p.PropertyType.Name} (CanWrite: {p.CanWrite})");
                foreach (var m in pile.GetType().GetMethods(AllInstance))
                {
                    if (!m.IsSpecialName)
                    {
                        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name}"));
                        ModEntry.Log($"  M: {m.Name}({parms}) : {m.ReturnType.Name}");
                    }
                }
            }

            // _cards 리스트 직접 조작 (silent) — NCard dispose 방지
            // 드로우/버린/소멸 더미는 화면에 안 보이므로 UI 이벤트 불필요
            var cardsField = pile.GetType().GetField("_cards", AllInstance);
            var cardsList = cardsField?.GetValue(pile) as System.Collections.IList;

            if (cardsList != null)
            {
                ModEntry.Log($"  CardPile 복원: 현재 {pile.Cards.Count}장 → {saved.Count}장");

                // 기존 카드 Unsubscribe (StateTracker)
                foreach (var card in pile.Cards.ToList())
                {
                    try
                    {
                        if (CombatManager.Instance?.IsInProgress == true)
                            CombatManager.Instance.StateTracker?.Unsubscribe(card);
                    }
                    catch { }
                }
                cardsList.Clear();

                for (int i = 0; i < saved.Count; i++)
                {
                    if (saved[i].CardRef != null)
                    {
                        // HasBeenRemovedFromState 리셋
                        try
                        {
                            var removedProp = saved[i].CardRef.GetType().GetProperty("HasBeenRemovedFromState", AllInstance);
                            if (removedProp != null && removedProp.CanWrite)
                            {
                                if ((bool)(removedProp.GetValue(saved[i].CardRef) ?? false))
                                    removedProp.SetValue(saved[i].CardRef, false);
                            }
                        }
                        catch { }

                        // _cards에 직접 추가 + StateTracker Subscribe
                        cardsList.Add(saved[i].CardRef);
                        try
                        {
                            if (CombatManager.Instance?.IsInProgress == true)
                                CombatManager.Instance.StateTracker?.Subscribe(saved[i].CardRef);
                        }
                        catch { }
                    }
                }
            }
            else
            {
                ModEntry.Log($"  카드 더미 복원 실패 - _cards 필드 없음");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"카드 더미 복원 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 카드더미의 모든 카드에서 NCard 이벤트 구독을 완전히 제거 (null로 설정).
    /// QueueFree된 NCard는 IsInstanceValid가 아직 true일 수 있으므로,
    /// disposed 체크 대신 Godot.Node 타겟을 가진 구독을 모두 제거.
    /// </summary>
    private void ClearAllNCardSubscriptions(CardPile pile)
    {
        try
        {
            int cleared = 0;
            foreach (var card in pile.Cards)
            {
                cleared += NullifyGodotDelegates(card, "AfflictionChanged");
                cleared += NullifyGodotDelegates(card, "EnchantmentChanged");
                if (card.Enchantment != null)
                    cleared += NullifyGodotDelegates(card.Enchantment, "StatusChanged");
            }
            if (cleared > 0)
                ModEntry.Log($"  NCard 구독 정리: {pile.Cards.Count}장에서 {cleared}개 Godot 구독 제거");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"  NCard 구독 정리 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 오브젝트의 이벤트/델리게이트 필드에서 Godot.GodotObject를 타겟으로 하는 모든 구독을 제거.
    /// disposed 여부와 관계없이 Godot 노드 타겟이면 전부 제거.
    /// </summary>
    private int NullifyGodotDelegates(object obj, string eventName)
    {
        try
        {
            var field = FindField(obj.GetType(), eventName);
            if (field == null) return 0;

            var del = field.GetValue(obj) as Delegate;
            if (del == null) return 0;

            var invList = del.GetInvocationList();
            int removedCount = 0;
            Delegate? newDel = null;

            foreach (var d in invList)
            {
                if (d.Target is Godot.GodotObject)
                {
                    removedCount++;
                }
                else
                {
                    newDel = newDel == null ? d : Delegate.Combine(newDel, d);
                }
            }

            if (removedCount > 0)
                field.SetValue(obj, newDel);

            return removedCount;
        }
        catch
        {
            return 0;
        }
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        var t = type;
        while (t != null && t != typeof(object))
        {
            var f = t.GetField(name, AllInstance | BindingFlags.DeclaredOnly);
            if (f != null) return f;
            t = t.BaseType;
        }
        return null;
    }

    /// <summary>
    /// 핸드 복원: CardPile 데이터 + NPlayerHand.Add(NCard) UI 동시 복원.
    /// </summary>
    private void RestoreHand(PlayerCombatState pcs, List<CardSnapshot> savedHand)
    {
        try
        {
            var hand = pcs.Hand;

            ModEntry.Log($"  핸드 복원: 현재 {hand.Cards.Count}장 → {savedHand.Count}장");

            // 1. CardPile 데이터 복원 — _cards 직접 조작 (Clear(false)는 NCard dispose 유발하므로 사용 금지!)
            var cardsField = hand.GetType().GetField("_cards", AllInstance);
            var cardsList = cardsField?.GetValue(hand) as System.Collections.IList;
            if (cardsList != null)
            {
                // 기존 카드 StateTracker에서 구독 해제
                foreach (var card in hand.Cards.ToList())
                {
                    try
                    {
                        if (CombatManager.Instance?.IsInProgress == true)
                            CombatManager.Instance.StateTracker?.Unsubscribe(card);
                    }
                    catch { }
                }
                cardsList.Clear();

                // 저장된 카드 추가
                for (int i = 0; i < savedHand.Count; i++)
                {
                    if (savedHand[i].CardRef != null)
                    {
                        try
                        {
                            // HasBeenRemovedFromState 리셋
                            try
                            {
                                var removedProp = savedHand[i].CardRef.GetType().GetProperty("HasBeenRemovedFromState", AllInstance);
                                if (removedProp != null && removedProp.CanWrite)
                                {
                                    if ((bool)(removedProp.GetValue(savedHand[i].CardRef) ?? false))
                                        removedProp.SetValue(savedHand[i].CardRef, false);
                                }
                            }
                            catch { }
                            cardsList.Add(savedHand[i].CardRef);
                            try
                            {
                                if (CombatManager.Instance?.IsInProgress == true)
                                    CombatManager.Instance.StateTracker?.Subscribe(savedHand[i].CardRef);
                            }
                            catch { }
                        }
                        catch (Exception ex)
                        {
                            ModEntry.Log($"  핸드 카드 추가 실패 [{i}] {savedHand[i].Id}: {ex.Message}");
                        }
                    }
                }
            }
            else
            {
                ModEntry.Log($"  핸드 복원 실패 - _cards 필드 없음, fallback to Clear");
                var clearMethod = hand.GetType().GetMethod("Clear", AllInstance);
                var addInternalMethod = hand.GetType().GetMethod("AddInternal", AllInstance);
                clearMethod?.Invoke(hand, new object[] { true }); // silent=true로 NCard dispose 방지
                for (int i = 0; i < savedHand.Count; i++)
                {
                    if (savedHand[i].CardRef != null)
                        addInternalMethod?.Invoke(hand, new object[] { savedHand[i].CardRef, i, true });
                }
            }
            var contentsChanged = hand.GetType().GetMethod("InvokeContentsChanged", AllInstance);
            contentsChanged?.Invoke(hand, null);

            // 2. UI 복원: NPlayerHand를 찾아서 NCard 생성/추가
            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            if (tree?.Root == null) return;

            var handNode = FindNodeByType(tree.Root, "NPlayerHand");
            if (handNode == null)
            {
                ModEntry.Log("  NPlayerHand 없음");
                return;
            }

            // NCard 씬 경로 확인 (기존 NCard에서 추출)
            var containerNode = FindNodeByName(handNode, "CardHolderContainer");
            if (containerNode == null)
            {
                ModEntry.Log("  CardHolderContainer 없음");
                return;
            }

            // 기존 NCard의 SceneFilePath와 타입 정보 수집 (1회)
            if (!_nCardLogged)
            {
                _nCardLogged = true;
                var existingNCard = FindNodeByType(containerNode, "NCard");
                if (existingNCard != null)
                {
                    ModEntry.Log($"  NCard 타입: {existingNCard.GetType().FullName}");
                    ModEntry.Log($"  NCard SceneFilePath: {existingNCard.SceneFilePath}");
                    foreach (var m in existingNCard.GetType().GetMethods(AllInstance)
                        .Where(m => !m.IsSpecialName && m.DeclaringType == existingNCard.GetType())
                        .OrderBy(m => m.Name))
                    {
                        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        ModEntry.Log($"    NCard M: {m.Name}({parms}) : {m.ReturnType.Name}");
                    }
                    foreach (var p in existingNCard.GetType().GetProperties(AllInstance)
                        .Where(p => p.DeclaringType == existingNCard.GetType()))
                    {
                        ModEntry.Log($"    NCard P: {p.Name} : {p.PropertyType.Name} (CanWrite: {p.CanWrite})");
                    }
                }
            }

            // 스냅샷의 CardRef 목록
            var savedCardRefs = new HashSet<CardModel>(
                savedHand.Where(c => c.CardRef != null).Select(c => c.CardRef!));

            // 현재 UI에 있는 CardModel 수집 + 스냅샷에 없는 카드 UI 제거
            var currentCardModels = new HashSet<CardModel>();
            var holdersToRemove = new List<Godot.Node>();
            for (int i = 0; i < containerNode.GetChildCount(); i++)
            {
                var holder = containerNode.GetChild(i);
                var cmProp = holder.GetType().GetProperty("CardModel", AllInstance);
                var cm = cmProp?.GetValue(holder) as CardModel;
                if (cm != null)
                {
                    currentCardModels.Add(cm);
                    if (!savedCardRefs.Contains(cm))
                        holdersToRemove.Add(holder);
                }
            }

            // 스냅샷에 없는 카드 UI 제거 (생성된 카드 등)
            foreach (var holder in holdersToRemove)
            {
                ModEntry.Log($"    불필요한 카드 UI 제거: {holder.Name}");
                holder.QueueFree();
            }

            // 기존 NCard들의 CardModel 이벤트 재구독 (ClearAllNCardSubscriptions으로 정리된 것 복원)
            for (int i = 0; i < containerNode.GetChildCount(); i++)
            {
                var holder = containerNode.GetChild(i);
                if (holdersToRemove.Contains(holder)) continue; // 제거 예정인 건 스킵

                // holder 안의 NCard 찾기
                Godot.Node? ncard = null;
                for (int j = 0; j < holder.GetChildCount(); j++)
                {
                    var child = holder.GetChild(j);
                    if (child.GetType().Name == "NCard") { ncard = child; break; }
                }
                if (ncard == null && holder.GetType().Name == "NCard") ncard = holder;
                if (ncard == null) continue;

                var cmProp2 = holder.GetType().GetProperty("CardModel", AllInstance);
                var cardModel = cmProp2?.GetValue(holder) as CardModel;
                if (cardModel != null && savedCardRefs.Contains(cardModel))
                {
                    try
                    {
                        var subscribeMethod = ncard.GetType().GetMethod("SubscribeToModel", AllInstance);
                        subscribeMethod?.Invoke(ncard, new object[] { cardModel });
                    }
                    catch { }
                }
            }

            // 누락된 카드 찾기 (원래 인덱스 보존)
            var missingCards = savedHand
                .Select((c, idx) => (card: c, index: idx))
                .Where(x => x.card.CardRef != null && !currentCardModels.Contains(x.card.CardRef!))
                .ToList();

            ModEntry.Log($"  현재 UI: {currentCardModels.Count}장, 제거: {holdersToRemove.Count}장, 누락: {missingCards.Count}장");

            if (missingCards.Count == 0) return;

            // NCard 씬 경로 찾기 (캐시 우선, 없으면 기존 NCard에서 추출)
            var existingCard = FindNodeByType(containerNode, "NCard");
            string? scenePath = existingCard?.SceneFilePath;
            var nCardType = existingCard?.GetType();

            // 캐시에 저장
            if (!string.IsNullOrEmpty(scenePath) && nCardType != null)
            {
                _cachedNCardScenePath = scenePath;
                _cachedNCardType = nCardType;
            }
            // 현재 컨테이너에 NCard가 없으면 캐시 사용
            else if (!string.IsNullOrEmpty(_cachedNCardScenePath))
            {
                scenePath = _cachedNCardScenePath;
                nCardType = _cachedNCardType;
                ModEntry.Log("  NCard 씬 경로를 캐시에서 사용");
            }

            if (string.IsNullOrEmpty(scenePath) || nCardType == null)
            {
                ModEntry.Log("  NCard 씬 경로를 찾을 수 없음");
                return;
            }

            ModEntry.Log($"  NCard 씬: {scenePath}");

            // PackedScene 로드
            var packedScene = Godot.ResourceLoader.Load<Godot.PackedScene>(scenePath);
            if (packedScene == null)
            {
                ModEntry.Log("  PackedScene 로드 실패");
                return;
            }

            // NPlayerHand.Add 메서드 (AmbiguousMatchException 방지)
            System.Reflection.MethodInfo? addMethod = null;
            try
            {
                addMethod = handNode.GetType().GetMethod("Add", AllInstance);
            }
            catch (System.Reflection.AmbiguousMatchException)
            {
                // 오버로드가 여러 개면 NCard + int 시그니처 찾기
                var addMethods = handNode.GetType().GetMethods(AllInstance)
                    .Where(m => m.Name == "Add").ToArray();
                foreach (var m in addMethods)
                {
                    var parms = m.GetParameters();
                    if (parms.Length == 2 && parms[1].ParameterType == typeof(int))
                    {
                        addMethod = m;
                        break;
                    }
                }
                addMethod ??= addMethods.FirstOrDefault();
                ModEntry.Log($"  Add 오버로드 {addMethods.Length}개 중 선택: {addMethod?.GetParameters().Length ?? -1} 파라미터");
            }
            if (addMethod == null)
            {
                ModEntry.Log("  NPlayerHand.Add 메서드 없음");
                return;
            }

            ModEntry.Log($"  Add 메서드 파라미터: {string.Join(", ", addMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))}");

            foreach (var (missing, originalIndex) in missingCards)
            {
                try
                {
                    // NCard 인스턴스 생성
                    var newNCard = packedScene.Instantiate();
                    if (newNCard == null)
                    {
                        ModEntry.Log($"    NCard 인스턴스 생성 실패: {missing.CardRef?.Id.Entry}");
                        continue;
                    }

                    // NCard.Model 프로퍼티 설정 + SubscribeToModel 호출
                    var modelProp = newNCard.GetType().GetProperty("Model", AllInstance);
                    if (modelProp == null || !modelProp.CanWrite)
                    {
                        ModEntry.Log($"    NCard.Model 프로퍼티 없음/쓰기불가: {missing.CardRef?.Id.Entry}");
                        newNCard.QueueFree();
                        continue;
                    }

                    modelProp.SetValue(newNCard, missing.CardRef);

                    // SubscribeToModel로 이벤트 연결
                    var subscribeMethod = newNCard.GetType().GetMethod("SubscribeToModel", AllInstance);
                    subscribeMethod?.Invoke(newNCard, new object[] { missing.CardRef! });

                    // Reload로 비주얼 갱신
                    var reloadMethod = newNCard.GetType().GetMethod("Reload", AllInstance);
                    reloadMethod?.Invoke(newNCard, null);

                    // 원래 인덱스에 추가 (씬 트리에 들어가야 _Ready 실행됨)
                    int insertIdx = Math.Min(originalIndex, containerNode.GetChildCount());
                    var addParams = addMethod.GetParameters();
                    if (addParams.Length == 2)
                        addMethod.Invoke(handNode, new object[] { newNCard, insertIdx });
                    else if (addParams.Length == 1)
                        addMethod.Invoke(handNode, new object[] { newNCard });
                    else
                        addMethod.Invoke(handNode, new object[] { newNCard, insertIdx });
                    ModEntry.Log($"    카드 추가 성공: {missing.CardRef?.Id.Entry} at index {insertIdx} (원래 {originalIndex})");

                    // 씬 트리 추가 후: SubscribeToModel 재호출 (이제 IsInsideTree()=true)
                    try
                    {
                        var subModelPost = newNCard.GetType().GetMethod("SubscribeToModel", AllInstance);
                        subModelPost?.Invoke(newNCard, new object[] { missing.CardRef! });

                        // UpdateVisuals(PileType.Hand, CardPreviewMode.Normal) 호출
                        var updateVisuals = newNCard.GetType().GetMethod("UpdateVisuals", AllInstance);
                        if (updateVisuals != null)
                        {
                            // PileType.Hand enum 값 찾기
                            var pileTypeEnum = typeof(CardModel).Assembly.GetType("MegaCrit.Sts2.Core.Entities.Cards.PileType");
                            if (pileTypeEnum != null)
                            {
                                var handVal = Enum.Parse(pileTypeEnum, "Hand");
                                var previewModeEnum = typeof(CardModel).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Cards.CardPreviewMode");
                                if (previewModeEnum != null)
                                {
                                    var normalVal = Enum.Parse(previewModeEnum, "Normal");
                                    updateVisuals.Invoke(newNCard, new object[] { handVal, normalVal });
                                }
                            }
                        }
                        if (missing.CardRef!.Enchantment != null)
                            ModEntry.Log($"    인챈트 비주얼 설정: {missing.CardRef.Enchantment.Id.Entry}");
                    }
                    catch (Exception enchEx)
                    {
                        ModEntry.Log($"    트리 후 구독/비주얼 실패: {enchEx.InnerException?.Message ?? enchEx.Message}");
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.Log($"    카드 추가 실패 ({missing.CardRef?.Id.Entry}): {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            ModEntry.Log($"  핸드 UI 복원 완료");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"핸드 복원 실패: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>카드 선택 UI (CardPicker, CardSelectionScreen 등)를 찾아서 닫기</summary>
    private void CloseCardSelectionUI(Godot.Node root)
    {
        try
        {
            // 다양한 이름으로 카드 선택 UI를 검색
            string[] pickerTypes = { "NCardPicker", "CardPicker", "CardSelectionScreen",
                "NCardSelectionPopup", "CardSelectionPopup", "NHandCardPicker" };

            foreach (var typeName in pickerTypes)
            {
                var picker = FindNodeByType(root, typeName);
                if (picker != null)
                {
                    ModEntry.Log($"  카드 선택 UI 발견: {picker.GetType().Name}");

                    // Close/Cancel/Hide 시도
                    var closeMethod = picker.GetType().GetMethod("Close", AllInstance)
                        ?? picker.GetType().GetMethod("Cancel", AllInstance)
                        ?? picker.GetType().GetMethod("Hide", AllInstance)
                        ?? picker.GetType().GetMethod("OnCancel", AllInstance)
                        ?? picker.GetType().GetMethod("Dismiss", AllInstance);

                    if (closeMethod != null)
                    {
                        var paramCount = closeMethod.GetParameters().Length;
                        if (paramCount == 0)
                            closeMethod.Invoke(picker, null);
                        else
                            closeMethod.Invoke(picker, new object[paramCount]);
                        ModEntry.Log($"    {closeMethod.Name}() 호출");
                    }

                    // Visible = false
                    var visibleProp = picker.GetType().GetProperty("Visible");
                    if (visibleProp != null && visibleProp.CanWrite)
                    {
                        visibleProp.SetValue(picker, false);
                        ModEntry.Log("    Visible = false");
                    }

                    // 내부 NCard도 제거
                    int removed = RemoveAllNCardsRecursive(picker);
                    if (removed > 0)
                        ModEntry.Log($"    선택 UI 내 NCard {removed}개 제거");

                    // 1회만 메서드 덤프
                    if (!_pickerLogged)
                    {
                        _pickerLogged = true;
                        DumpNodeInfo(picker, "CardPicker");
                    }
                }
            }

            // "HandCardSelectionOverlay" 등 오버레이도 검색
            string[] overlayTypes = { "HandCardSelectionOverlay", "NHandCardSelectionOverlay",
                "CardSelectionOverlay" };
            foreach (var typeName in overlayTypes)
            {
                var overlay = FindNodeByType(root, typeName);
                if (overlay != null)
                {
                    ModEntry.Log($"  오버레이 발견: {overlay.GetType().Name}");
                    var closeMeth = overlay.GetType().GetMethod("Close", AllInstance)
                        ?? overlay.GetType().GetMethod("Cancel", AllInstance);
                    if (closeMeth != null)
                    {
                        try { closeMeth.Invoke(overlay, closeMeth.GetParameters().Length == 0 ? null : new object[closeMeth.GetParameters().Length]); }
                        catch { }
                    }
                    var visProp = overlay.GetType().GetProperty("Visible");
                    if (visProp != null && visProp.CanWrite)
                        visProp.SetValue(overlay, false);
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"  카드 선택 UI 닫기 실패 (무시): {ex.Message}");
        }
    }

    /// <summary>노드 하위의 모든 NCard를 재귀적으로 제거</summary>
    private int RemoveAllNCardsRecursive(Godot.Node parent)
    {
        int count = 0;
        var toRemove = new List<Godot.Node>();
        CollectNCards(parent, toRemove);

        foreach (var node in toRemove)
        {
            try
            {
                var p = node.GetParent();
                p?.RemoveChild(node);
                node.Free();
                count++;
            }
            catch { }
        }
        return count;
    }

    private void CollectNCards(Godot.Node node, List<Godot.Node> result)
    {
        // NCard이면 수집 (자식은 탐색하지 않음 - NCard 하위에 NCard는 없을 것)
        if (node.GetType().Name == "NCard" || node.GetType().Name.EndsWith("NCard"))
        {
            result.Add(node);
            return;
        }
        for (int i = 0; i < node.GetChildCount(); i++)
            CollectNCards(node.GetChild(i), result);
    }

    /// <summary>root 하위에서 excludeSubtree 바깥의 NCard를 찾기</summary>
    private List<Godot.Node> FindAllNCardsByType(Godot.Node root, Godot.Node? excludeSubtree)
    {
        var result = new List<Godot.Node>();
        FindNCardsExcluding(root, excludeSubtree, result);
        return result;
    }

    private void FindNCardsExcluding(Godot.Node node, Godot.Node? exclude, List<Godot.Node> result)
    {
        if (node == exclude) return;
        if (node.GetType().Name == "NCard" || node.GetType().Name.EndsWith("NCard"))
        {
            result.Add(node);
            return;
        }
        for (int i = 0; i < node.GetChildCount(); i++)
            FindNCardsExcluding(node.GetChild(i), exclude, result);
    }

    /// <summary>노드의 모든 메서드/프로퍼티/필드 로깅</summary>
    private void DumpNodeInfo(Godot.Node node, string label)
    {
        var type = node.GetType();
        ModEntry.Log($"=== {label} ({type.FullName}) ===");

        // 메서드
        foreach (var m in type.GetMethods(AllInstance)
            .Where(m => !m.IsSpecialName && m.DeclaringType == type)
            .OrderBy(m => m.Name))
        {
            var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            ModEntry.Log($"  M: {m.Name}({parms}) : {m.ReturnType.Name}");
        }

        // 프로퍼티
        foreach (var p in type.GetProperties(AllInstance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name))
        {
            ModEntry.Log($"  P: {p.Name} : {p.PropertyType.Name}");
        }
    }

    /// <summary>노드 트리 구조 로깅 (최대 3레벨)</summary>
    private void DumpNodeTree(Godot.Node node, string label, int depth)
    {
        if (depth > 3) return;
        var indent = new string(' ', depth * 2);
        var childCount = node.GetChildCount();
        ModEntry.Log($"  {indent}{label} [{node.GetType().Name}] children={childCount}");
        for (int i = 0; i < childCount; i++)
        {
            var child = node.GetChild(i);
            DumpNodeTree(child, child.Name, depth + 1);
        }
    }

    private static bool _nCardLogged;
    private static bool _pickerLogged;
    private static bool _handTreeLogged;
    private static string? _cachedNCardScenePath;
    private static Type? _cachedNCardType;

    private static Godot.Node? FindNodeByType(Godot.Node root, string typeName)
    {
        if (root.GetType().Name == typeName) return root;
        for (int i = 0; i < root.GetChildCount(); i++)
        {
            var child = root.GetChild(i);
            var found = FindNodeByType(child, typeName);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>특정 Creature에 대응하는 NCreature 노드를 찾기</summary>
    private static Godot.Node? FindNCreatureForMonster(Godot.Node root, Creature creature)
    {
        var nodes = new List<Godot.Node>();
        FindAllNodesByType(root, "NCreature", nodes);
        foreach (var node in nodes)
        {
            try
            {
                var creatureProp = node.GetType().GetProperty("Creature", AllInstance);
                if (creatureProp != null)
                {
                    var c = creatureProp.GetValue(node) as Creature;
                    if (c == creature) return node;
                }
            }
            catch { }
        }
        return null;
    }

    private static void FindAllNodesByType(Godot.Node root, string typeName, List<Godot.Node> result)
    {
        if (root.GetType().Name == typeName)
            result.Add(root);
        for (int i = 0; i < root.GetChildCount(); i++)
            FindAllNodesByType(root.GetChild(i), typeName, result);
    }

    private static Godot.Node? FindNodeByName(Godot.Node root, string nodeName)
    {
        if (root.Name == nodeName) return root;
        for (int i = 0; i < root.GetChildCount(); i++)
        {
            var found = FindNodeByName(root.GetChild(i), nodeName);
            if (found != null) return found;
        }
        return null;
    }

    private static bool _potionHolderLogged;
    private static bool _potionPopupLogged;
    private static bool _potionMethodLogged;
    private static bool _potionUILogged;

    private static Godot.Node? FindNodeByTypeContains(Godot.Node root, string partial)
    {
        if (root.GetType().Name.Contains(partial)) return root;
        for (int i = 0; i < root.GetChildCount(); i++)
        {
            var found = FindNodeByTypeContains(root.GetChild(i), partial);
            if (found != null) return found;
        }
        return null;
    }

    private static void FindAllByTypeContains(Godot.Node root, string partial, List<Godot.Node> results)
    {
        if (root.GetType().Name.Contains(partial)) results.Add(root);
        for (int i = 0; i < root.GetChildCount(); i++)
            FindAllByTypeContains(root.GetChild(i), partial, results);
    }

    private static void FindAllByType(Godot.Node root, string typeName, List<Godot.Node> results)
    {
        if (root.GetType().Name == typeName) results.Add(root);
        for (int i = 0; i < root.GetChildCount(); i++)
            FindAllByType(root.GetChild(i), typeName, results);
    }

    private static void DumpNodeTree(Godot.Node node, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        var indent = new string(' ', depth * 2);
        ModEntry.Log($"{indent}[{node.GetType().Name}] {node.Name} (자식: {node.GetChildCount()})");
        for (int i = 0; i < node.GetChildCount(); i++)
            DumpNodeTree(node.GetChild(i), depth + 1, maxDepth);
    }

    /// <summary>NPotionPopup을 복원 (다른 홀더의 팝업 씬으로 새로 생성)</summary>
    private static void RestorePotionPopup(Godot.Node holder, List<Godot.Node> allHolders)
    {
        try
        {
            var popupField = holder.GetType().GetField("_popup", AllInstance);
            if (popupField == null) return;

            var currentPopup = popupField.GetValue(holder) as Godot.GodotObject;
            if (currentPopup != null && Godot.GodotObject.IsInstanceValid(currentPopup))
            {
                ModEntry.Log($"  _popup 이미 유효함");
                return;
            }

            // 다른 홀더에서 유효한 팝업의 씬 경로 찾기
            string? scenePath = null;
            foreach (var otherHolder in allHolders)
            {
                if (otherHolder == holder) continue;
                var otherPopup = popupField.GetValue(otherHolder) as Godot.Node;
                if (otherPopup != null && Godot.GodotObject.IsInstanceValid(otherPopup))
                {
                    scenePath = otherPopup.SceneFilePath;
                    ModEntry.Log($"  다른 홀더 팝업 ScenePath: {scenePath}");

                    // 씬 경로 없으면 팝업이 코드로 생성된 것 → 직접 인스턴스화
                    if (string.IsNullOrEmpty(scenePath))
                    {
                        // 팝업 타입으로 직접 생성
                        var popupType = otherPopup.GetType();
                        ModEntry.Log($"  팝업 타입: {popupType.FullName}");

                        // Godot 씬 기반이 아닌 경우, 팝업의 부모 씬에서 찾기
                        // NPotionHolder의 씬 자체에 포함되어있을 수 있음
                        var holderScenePath = holder.SceneFilePath;
                        ModEntry.Log($"  홀더 ScenePath: {holderScenePath}");
                    }
                    break;
                }
            }

            if (!string.IsNullOrEmpty(scenePath))
            {
                var packedScene = Godot.ResourceLoader.Load<Godot.PackedScene>(scenePath);
                if (packedScene != null)
                {
                    var newPopup = packedScene.Instantiate();
                    popupField.SetValue(holder, newPopup);
                    holder.AddChild(newPopup);
                    ModEntry.Log($"  포션 팝업 씬에서 생성 완료");
                    return;
                }
            }

            // 씬 경로가 없는 경우: 홀더 씬을 재인스턴스화해서 팝업만 빼오기
            var holderScene = holder.SceneFilePath;
            if (!string.IsNullOrEmpty(holderScene))
            {
                var packed = Godot.ResourceLoader.Load<Godot.PackedScene>(holderScene);
                if (packed != null)
                {
                    var tempHolder = packed.Instantiate();
                    var tempPopup = popupField.GetValue(tempHolder) as Godot.Node;
                    if (tempPopup != null)
                    {
                        tempPopup.GetParent()?.RemoveChild(tempPopup);
                        popupField.SetValue(holder, tempPopup);
                        holder.AddChild(tempPopup);
                        ModEntry.Log($"  홀더 씬에서 팝업 추출 완료");
                    }
                    tempHolder.QueueFree();
                    return;
                }
            }

            // 최후 수단: 팝업 타입을 직접 new로 생성
            foreach (var otherHolder in allHolders)
            {
                if (otherHolder == holder) continue;
                var otherPopup = popupField.GetValue(otherHolder) as Godot.Node;
                if (otherPopup != null && Godot.GodotObject.IsInstanceValid(otherPopup))
                {
                    var popupType = otherPopup.GetType();
                    try
                    {
                        var newPopup = Activator.CreateInstance(popupType) as Godot.Node;
                        if (newPopup != null)
                        {
                            popupField.SetValue(holder, newPopup);
                            holder.AddChild(newPopup);
                            ModEntry.Log($"  포션 팝업 new 생성 완료: {popupType.Name}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Log($"  팝업 new 생성 실패: {ex.Message}");
                    }
                    break;
                }
            }

            ModEntry.Log($"  포션 팝업 복원 실패 - 유효한 팝업 없음");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"  포션 팝업 복원 오류: {ex.Message}");
        }
    }

    /// <summary>포션 홀더를 사용 가능 상태로 복원</summary>
    private static void ReactivatePotionHolder(Godot.Node holder, int slotIdx)
    {
        try
        {
            // 1회만 NPotionHolder 필드 덤프
            if (!_potionHolderLogged)
            {
                _potionHolderLogged = true;
                var t = holder.GetType();
                ModEntry.Log($"=== NPotionHolder 필드 ===");
                while (t != null && t != typeof(Godot.Node))
                {
                    foreach (var f in t.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                        ModEntry.Log($"  F: {f.Name} : {f.FieldType.Name} = {f.GetValue(holder)}");
                    foreach (var p in t.GetProperties(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        try { ModEntry.Log($"  P: {p.Name} : {p.PropertyType.Name} = {p.GetValue(holder)}"); }
                        catch { ModEntry.Log($"  P: {p.Name} : {p.PropertyType.Name} = (error)"); }
                    }
                    t = t.BaseType;
                }
            }

            // _disabledUntilPotionRemoved 등 bool 필드 직접 리셋
            var holderType = holder.GetType();
            foreach (var f in holderType.GetFields(AllInstance | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType == typeof(bool))
                {
                    var fn = f.Name.ToLower();
                    if (fn.Contains("disable") || fn.Contains("gray") || fn.Contains("used") ||
                        fn.Contains("cancel") || fn.Contains("focused"))
                    {
                        f.SetValue(holder, false);
                        ModEntry.Log($"  포션 홀더 필드 리셋: {f.Name} = false (슬롯{slotIdx})");
                    }
                }
            }

            // _popup(NPotionPopup)이 disposed/null이면 재생성
            var popupField = holderType.GetField("_popup", AllInstance);
            if (popupField != null)
            {
                bool needsPopup = false;
                var currentPopup = popupField.GetValue(holder);
                if (currentPopup == null)
                {
                    needsPopup = true;
                }
                else
                {
                    // disposed 체크: Godot.GodotObject.IsInstanceValid
                    try
                    {
                        needsPopup = !Godot.GodotObject.IsInstanceValid(currentPopup as Godot.GodotObject);
                    }
                    catch { needsPopup = true; }
                }

                if (needsPopup)
                {
                    ModEntry.Log($"  포션 팝업 재생성 필요 (슬롯{slotIdx})");

                    // 다른 홀더에서 유효한 팝업의 씬 경로 찾기
                    string? popupScenePath = null;
                    var potionContainer = holder.GetParent()?.GetParent()?.GetParent(); // HBoxContainer → MarginContainer → NPotionContainer
                    if (potionContainer == null)
                        potionContainer = FindNodeByType(
                            (Godot.Engine.GetMainLoop() as Godot.SceneTree)!.Root, "NPotionContainer");

                    if (potionContainer != null)
                    {
                        var allHolders = new List<Godot.Node>();
                        FindAllByType(potionContainer, "NPotionHolder", allHolders);
                        foreach (var otherHolder in allHolders)
                        {
                            if (otherHolder == holder) continue;
                            var otherPopup = popupField.GetValue(otherHolder) as Godot.Node;
                            if (otherPopup != null && Godot.GodotObject.IsInstanceValid(otherPopup))
                            {
                                popupScenePath = otherPopup.SceneFilePath;
                                if (!string.IsNullOrEmpty(popupScenePath)) break;

                                // 씬 경로가 없으면 타입에서 추정
                                if (!_potionPopupLogged)
                                {
                                    _potionPopupLogged = true;
                                    ModEntry.Log($"  유효한 팝업 발견: [{otherPopup.GetType().Name}] ScenePath={otherPopup.SceneFilePath}");
                                    // 팝업의 생성 방식 확인
                                    foreach (var m in otherPopup.GetType().GetMethods(AllInstance)
                                        .Where(m => !m.IsSpecialName && m.DeclaringType == otherPopup.GetType()))
                                    {
                                        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                                        ModEntry.Log($"    팝업 M: {m.Name}({parms})");
                                    }
                                }
                                break;
                            }
                        }
                    }

                    // OpenPotionPopup을 다시 호출하면 팝업을 새로 만들 수 있는지 시도
                    // → _Ready에서 생성했을 수 있으니 _Ready 재호출
                    var readyMethod = holderType.GetMethod("_Ready", AllInstance);
                    if (readyMethod != null)
                    {
                        try
                        {
                            readyMethod.Invoke(holder, null);
                            ModEntry.Log($"  NPotionHolder._Ready() 재호출 (슬롯{slotIdx})");

                            // _Ready 후 팝업 상태 확인
                            var newPopup = popupField.GetValue(holder);
                            ModEntry.Log($"  _Ready 후 _popup: {(newPopup != null ? "있음" : "없음")}");
                        }
                        catch (Exception ex)
                        {
                            ModEntry.Log($"  _Ready 호출 실패: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                }
            }

            // Godot Control 상태 리셋
            if (holder is Godot.Control ctrl)
            {
                ctrl.MouseFilter = Godot.Control.MouseFilterEnum.Stop;
                ctrl.Modulate = new Godot.Color(1, 1, 1, 1);
                ctrl.SelfModulate = new Godot.Color(1, 1, 1, 1);
                ctrl.Visible = true;
                ctrl.ProcessMode = Godot.Node.ProcessModeEnum.Inherit;
            }

            ModEntry.Log($"  포션 홀더 활성화: 슬롯{slotIdx}");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"  포션 홀더 활성화 실패 (슬롯{slotIdx}): {ex.Message}");
        }
    }

    // 유물 타입별 backing 필드 캐시
    private static readonly Dictionary<string, (Type declaringType, string fieldName)?> _relicFieldCache = new();

    /// <summary>유물의 카운터 backing 필드를 찾아 RelicSnapshot에 저장 (캡처 시점)</summary>
    private static void FindRelicCounterField(RelicModel relic, RelicSnapshot rs)
    {
        var relicTypeName = relic.GetType().FullName ?? relic.GetType().Name;

        // 캐시에 있으면 재사용
        if (_relicFieldCache.TryGetValue(relicTypeName, out var cached))
        {
            if (cached != null)
            {
                rs.CounterFieldName = cached.Value.fieldName;
                rs.CounterFieldDeclaringType = cached.Value.declaringType;
            }
            return;
        }

        int displayAmount = relic.DisplayAmount;
        if (displayAmount == 0)
        {
            // DisplayAmount가 0이면 필드 식별이 어려움 — 캐시하지 않고 다음 기회에 시도
            return;
        }

        // 서브클래스부터 상위로 int 필드 검색
        var t = relic.GetType();
        while (t != null && t != typeof(object))
        {
            foreach (var f in t.GetFields(AllInstance | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType == typeof(int))
                {
                    int val = (int)f.GetValue(relic)!;
                    if (val == displayAmount)
                    {
                        rs.CounterFieldName = f.Name;
                        rs.CounterFieldDeclaringType = t;
                        _relicFieldCache[relicTypeName] = (t, f.Name);
                        ModEntry.Log($"    유물 카운터 필드 발견: {t.Name}.{f.Name} (값={val})");
                        return;
                    }
                }
            }
            t = t.BaseType;
        }

        // 못 찾음
        _relicFieldCache[relicTypeName] = null;
    }

    /// <summary>유물의 내부 카운터 필드를 복원</summary>
    private static void RestoreRelicCounter(RelicSnapshot rs)
    {
        var relic = rs.RelicRef!;
        var targetValue = rs.Counter;

        // 스냅샷에 저장된 필드명이 있으면 직접 사용
        if (rs.CounterFieldName != null && rs.CounterFieldDeclaringType != null)
        {
            var field = rs.CounterFieldDeclaringType.GetField(rs.CounterFieldName, AllInstance);
            if (field != null)
            {
                field.SetValue(relic, targetValue);
                ModEntry.Log($"    유물 필드 설정: {rs.CounterFieldDeclaringType.Name}.{rs.CounterFieldName} = {targetValue}");
                InvokeRelicDisplayChanged(relic);
                return;
            }
        }

        // 캐시에서 시도
        var relicTypeName = relic.GetType().FullName ?? relic.GetType().Name;
        if (_relicFieldCache.TryGetValue(relicTypeName, out var cached) && cached != null)
        {
            var field = cached.Value.declaringType.GetField(cached.Value.fieldName, AllInstance);
            if (field != null)
            {
                field.SetValue(relic, targetValue);
                ModEntry.Log($"    유물 필드 설정 (캐시): {cached.Value.declaringType.Name}.{cached.Value.fieldName} = {targetValue}");
                InvokeRelicDisplayChanged(relic);
                return;
            }
        }

        // 폴백: 현재 값 매칭 (기존 방식)
        var relicType = relic.GetType();
        int currentDisplay = relic.DisplayAmount;
        var t = relicType;
        while (t != null && t != typeof(object))
        {
            foreach (var f in t.GetFields(AllInstance | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType == typeof(int))
                {
                    int val = (int)f.GetValue(relic)!;
                    if (val == currentDisplay)
                    {
                        f.SetValue(relic, targetValue);
                        ModEntry.Log($"    유물 필드 설정 (폴백): {t.Name}.{f.Name} = {targetValue}");
                        _relicFieldCache[relicTypeName] = (t, f.Name);
                        InvokeRelicDisplayChanged(relic);
                        return;
                    }
                }
            }
            t = t.BaseType;
        }

        ModEntry.Log($"    유물 카운터 필드를 찾지 못함: {relicType.Name}");
    }

    /// <summary>유물별 추가 bool/int 필드를 캡처 (턴 중 변경되는 내부 상태)</summary>
    /// <summary>파워의 _internalData 내부 필드를 캡처</summary>
    private static void CapturePowerInternalData(PowerModel power, PowerSnapshot ps)
    {
        try
        {
            var dataField = typeof(PowerModel).GetField("_internalData", AllInstance);
            if (dataField == null) return;
            var data = dataField.GetValue(power);
            if (data == null) return;

            // MemberwiseClone으로 _internalData 원자적 복사 (참고 레포 방식)
            try
            {
                var cloneMethod = data.GetType().GetMethod("MemberwiseClone", AllInstance);
                if (cloneMethod != null)
                    ps.InternalDataClone = cloneMethod.Invoke(data, null);
            }
            catch { }

            var dataType = data.GetType();
            foreach (var f in dataType.GetFields(AllInstance | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType == typeof(bool))
                    ps.InternalDataFields[f.Name] = (bool)f.GetValue(data)!;
                else if (f.FieldType == typeof(int))
                    ps.InternalDataFields[f.Name] = (int)f.GetValue(data)!;
                else if (f.FieldType == typeof(decimal))
                    ps.InternalDataFields[f.Name] = (decimal)f.GetValue(data)!;
            }
        }
        catch { }

        // 파워 서브클래스 고유 필드 캡처 (단단한 껍질 등)
        try
        {
            var powerType = power.GetType();
            var baseType = typeof(PowerModel);
            var subType = powerType;
            while (subType != null && subType != baseType && subType != typeof(object))
            {
                foreach (var f in subType.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                {
                    try
                    {
                        var val = f.GetValue(power);
                        if (val is int || val is decimal || val is bool || val is float || val is double)
                        {
                            ps.InternalDataFields[$"__sub_{f.Name}"] = val;
                        }
                    }
                    catch { }
                }
                subType = subType.BaseType;
            }
        }
        catch { }
    }

    /// <summary>파워의 _internalData 내부 필드 + 서브클래스 고유 필드를 복원</summary>
    private static void RestorePowerInternalData(PowerModel power, PowerSnapshot ps)
    {
        try
        {
            var dataField = typeof(PowerModel).GetField("_internalData", AllInstance);

            // MemberwiseClone 원자적 복원 (최우선)
            if (ps.InternalDataClone != null && dataField != null)
            {
                try
                {
                    // 스냅샷 보호를 위해 클론의 클론을 사용
                    var cloneMethod = ps.InternalDataClone.GetType().GetMethod("MemberwiseClone", AllInstance);
                    var freshClone = cloneMethod?.Invoke(ps.InternalDataClone, null);
                    if (freshClone != null)
                    {
                        dataField.SetValue(power, freshClone);
                        ModEntry.Log($"    파워 _internalData MemberwiseClone 복원: {ps.Id}");
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.Log($"    파워 _internalData 클론 복원 실패 ({ps.Id}): {ex.Message}");
                }
            }
            else if (ps.InternalDataFields.Count > 0)
            {
                // 클론이 없을 경우 필드별 복원 (폴백)
                var data = dataField?.GetValue(power);
                foreach (var kvp in ps.InternalDataFields)
                {
                    if (kvp.Key.StartsWith("__sub_")) continue;  // 서브클래스는 아래에서 별도 처리
                    if (data == null) continue;
                    var dataType = data.GetType();
                    var f = dataType.GetField(kvp.Key, AllInstance | BindingFlags.DeclaredOnly);
                    if (f == null) continue;
                    var currentVal = f.GetValue(data);
                    if (!Equals(currentVal, kvp.Value))
                    {
                        f.SetValue(data, kvp.Value);
                        ModEntry.Log($"    파워 내부 데이터 복원: {ps.Id}.{kvp.Key} = {currentVal} → {kvp.Value}");
                    }
                }
            }

            // 서브클래스 고유 필드 복원
            foreach (var kvp in ps.InternalDataFields)
            {
                if (!kvp.Key.StartsWith("__sub_")) continue;
                var fieldName = kvp.Key.Substring(6);
                var f = FindFieldInHierarchy(power.GetType(), fieldName);
                if (f != null && kvp.Value != null)
                {
                    try
                    {
                        var currentVal = f.GetValue(power);
                        if (!Equals(currentVal, kvp.Value))
                        {
                            f.SetValue(power, Convert.ChangeType(kvp.Value, f.FieldType));
                            ModEntry.Log($"    파워 서브필드 복원: {ps.Id}.{fieldName} = {currentVal} → {kvp.Value}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Log($"    파워 서브필드 복원 실패: {ps.Id}.{fieldName}: {ex.Message}");
                    }
                }
            }

            // DisplayAmount + AmountChanged 갱신
            try
            {
                var evt = typeof(PowerModel).GetField("DisplayAmountChanged", AllInstance);
                var handler = evt?.GetValue(power) as Action;
                handler?.Invoke();
            }
            catch { }
            try
            {
                var evt2 = typeof(PowerModel).GetField("AmountChanged", AllInstance);
                var handler2 = evt2?.GetValue(power) as Action;
                handler2?.Invoke();
            }
            catch { }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"    파워 내부 데이터 복원 실패 ({ps.Id}): {ex.Message}");
        }
    }

    private static void CaptureRelicExtraFields(RelicModel relic, RelicSnapshot rs)
    {
        try
        {
            // 유물의 concrete 타입에서 private bool/int 필드를 모두 캡처
            var relicType = relic.GetType();
            foreach (var f in relicType.GetFields(AllInstance | BindingFlags.DeclaredOnly))
            {
                if (f.FieldType == typeof(bool))
                {
                    rs.ExtraFields[f.Name] = (bool)f.GetValue(relic)!;
                }
                else if (f.FieldType == typeof(int) && f.Name != rs.CounterFieldName)
                {
                    rs.ExtraFields[f.Name] = (int)f.GetValue(relic)!;
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"  유물 추가 필드 캡처 실패 ({rs.Id}): {ex.Message}");
        }
    }

    /// <summary>유물별 추가 필드를 복원</summary>
    private static void RestoreRelicExtraFields(RelicSnapshot rs)
    {
        if (rs.RelicRef == null || rs.ExtraFields.Count == 0) return;
        var relic = rs.RelicRef;
        var relicType = relic.GetType();

        foreach (var kvp in rs.ExtraFields)
        {
            try
            {
                var field = relicType.GetField(kvp.Key, AllInstance | BindingFlags.DeclaredOnly);
                if (field == null) continue;

                var currentVal = field.GetValue(relic);
                if (!Equals(currentVal, kvp.Value))
                {
                    field.SetValue(relic, kvp.Value);
                    ModEntry.Log($"  유물 필드 복원: {rs.Id}.{kvp.Key} = {currentVal} → {kvp.Value}");
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  유물 필드 복원 실패 ({rs.Id}.{kvp.Key}): {ex.Message}");
            }
        }
    }

    private static void InvokeRelicDisplayChanged(RelicModel relic)
    {
        var evt = typeof(RelicModel).GetField("DisplayAmountChanged", AllInstance);
        var handler = evt?.GetValue(relic) as Action;
        handler?.Invoke();
    }

    /// <summary>복원 후 시스템 상태 진단 덤프</summary>
    private void DumpSystemState(string context, CombatState state, bool crossTurn)
    {
        try
        {
            ModEntry.Log($"\n=== 진단 덤프: {context} (crossTurn={crossTurn}) ===");

            // 1. CombatManager 상태
            var cm = CombatManager.Instance;
            if (cm != null)
            {
                var cmType = cm.GetType();
                var flags = AllInstance;
                ModEntry.Log($"  [CombatManager]");
                foreach (var pName in new[] { "IsPlayPhase", "EndingPlayerTurnPhaseOne", "EndingPlayerTurnPhaseTwo",
                    "IsEnemyTurnStarted", "IsPaused", "PlayerActionsDisabled", "IsInProgress" })
                {
                    try
                    {
                        var p = cmType.GetProperty(pName, flags);
                        if (p != null) ModEntry.Log($"    {pName} = {p.GetValue(cm)}");
                    }
                    catch { }
                }
                // _playerActionsDisabled 백킹필드
                try
                {
                    var f = cmType.GetField("_playerActionsDisabled", flags);
                    if (f != null) ModEntry.Log($"    _playerActionsDisabled (field) = {f.GetValue(cm)}");
                }
                catch { }
                // _playersReadyToEndTurn
                try
                {
                    var f = cmType.GetField("_playersReadyToEndTurn", flags);
                    var hs = f?.GetValue(cm);
                    var countProp = hs?.GetType().GetProperty("Count");
                    ModEntry.Log($"    _playersReadyToEndTurn.Count = {countProp?.GetValue(hs)}");
                }
                catch { }
            }

            // 2. CombatState
            ModEntry.Log($"  [CombatState]");
            ModEntry.Log($"    RoundNumber = {state.RoundNumber}");
            try
            {
                var csProp = state.GetType().GetProperty("CurrentSide", AllInstance);
                if (csProp != null) ModEntry.Log($"    CurrentSide = {csProp.GetValue(state)}");
            }
            catch { }

            // 3. ActionQueueSynchronizer
            try
            {
                var runMgrType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager");
                var runInstance = runMgrType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (runInstance != null)
                {
                    var aqsProp = runInstance.GetType().GetProperty("ActionQueueSynchronizer", AllInstance);
                    var aqsInstance = aqsProp?.GetValue(runInstance);
                    if (aqsInstance != null)
                    {
                        ModEntry.Log($"  [ActionQueueSynchronizer]");
                        var csProperty = aqsInstance.GetType().GetProperty("CombatState", AllInstance);
                        ModEntry.Log($"    CombatState = {csProperty?.GetValue(aqsInstance)}");

                        // _requestedActionsWaitingForPlayerTurn
                        var waitField = aqsInstance.GetType().GetField("_requestedActionsWaitingForPlayerTurn", AllInstance);
                        if (waitField != null)
                        {
                            var waitList = waitField.GetValue(aqsInstance);
                            var countProp = waitList?.GetType().GetProperty("Count");
                            ModEntry.Log($"    _requestedActionsWaitingForPlayerTurn.Count = {countProp?.GetValue(waitList)}");
                        }
                    }

                    // ActionExecutor
                    var execProp = runInstance.GetType().GetProperty("ActionExecutor", AllInstance);
                    var execInstance = execProp?.GetValue(runInstance);
                    if (execInstance != null)
                    {
                        ModEntry.Log($"  [ActionExecutor]");
                        var isPausedProp = execInstance.GetType().GetProperty("IsPaused", AllInstance);
                        ModEntry.Log($"    IsPaused = {isPausedProp?.GetValue(execInstance)}");
                        var currentAction = execInstance.GetType().GetProperty("CurrentlyRunningAction", AllInstance);
                        var actionVal = currentAction?.GetValue(execInstance);
                        ModEntry.Log($"    CurrentlyRunningAction = {actionVal?.GetType().Name ?? "null"}");
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  ActionSystem 덤프 실패: {ex.Message}");
            }

            // 4. ActionQueueSet
            try
            {
                var aqsType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.GameActions.ActionQueueSet");
                if (aqsType != null && cm != null)
                {
                    var aqsField = cm.GetType().GetField("_actionQueues", AllInstance)
                        ?? cm.GetType().GetFields(AllInstance).FirstOrDefault(f => f.FieldType == aqsType || f.FieldType.Name.Contains("ActionQueueSet"));
                    if (aqsField != null)
                    {
                        var aqs = aqsField.GetValue(cm);
                        if (aqs != null)
                        {
                            ModEntry.Log($"  [ActionQueueSet]");
                            foreach (var f in aqs.GetType().GetFields(AllInstance))
                            {
                                var fn = f.Name.ToLower();
                                if (fn.Contains("pause") || fn.Contains("cancel") || fn.Contains("queue") || fn.Contains("count"))
                                {
                                    try { ModEntry.Log($"    {f.Name} = {f.GetValue(aqs)}"); } catch { }
                                }
                            }
                            foreach (var p in aqs.GetType().GetProperties(AllInstance))
                            {
                                var pn = p.Name.ToLower();
                                if (pn.Contains("pause") || pn.Contains("cancel") || pn.Contains("queue") || pn.Contains("count"))
                                {
                                    try { ModEntry.Log($"    {p.Name} = {p.GetValue(aqs)}"); } catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"  ActionQueueSet 덤프 실패: {ex.Message}");
            }

            // 5. NEndTurnButton 상태
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree?.Root != null)
                {
                    var btn = FindNodeByType(tree.Root, "NEndTurnButton");
                    if (btn != null)
                    {
                        ModEntry.Log($"  [NEndTurnButton]");
                        ModEntry.Log($"    Visible = {btn.GetType().GetProperty("Visible")?.GetValue(btn)}");
                        var canEndProp = btn.GetType().GetProperty("CanTurnBeEnded", AllInstance);
                        if (canEndProp != null) ModEntry.Log($"    CanTurnBeEnded = {canEndProp.GetValue(btn)}");
                        var combatStateFld = btn.GetType().GetField("_combatState", AllInstance);
                        if (combatStateFld != null) ModEntry.Log($"    _combatState = {combatStateFld.GetValue(btn)}");
                    }
                }
            }
            catch { }

            // 6. 카드 파일 상태
            try
            {
                var player = state.Players.FirstOrDefault();
                var pcs = player?.PlayerCombatState;
                if (pcs != null)
                {
                    ModEntry.Log($"  [CardPiles]");
                    ModEntry.Log($"    Hand = {pcs.Hand.Cards.Count}장");
                    ModEntry.Log($"    DrawPile = {pcs.DrawPile.Cards.Count}장");
                    ModEntry.Log($"    DiscardPile = {pcs.DiscardPile.Cards.Count}장");
                    ModEntry.Log($"    ExhaustPile = {pcs.ExhaustPile.Cards.Count}장");

                    // 핸드 카드들의 HasBeenRemovedFromState 체크
                    foreach (var card in pcs.Hand.Cards)
                    {
                        var removed = card.GetType().GetProperty("HasBeenRemovedFromState", AllInstance)?.GetValue(card);
                        if (removed is true)
                            ModEntry.Log($"    [경고] 핸드 카드 {card.Id.Entry}의 HasBeenRemovedFromState = true!");
                    }
                    // 뽑기 더미 카드들도 체크
                    int removedInDraw = 0;
                    foreach (var card in pcs.DrawPile.Cards)
                    {
                        var removed = card.GetType().GetProperty("HasBeenRemovedFromState", AllInstance)?.GetValue(card);
                        if (removed is true) removedInDraw++;
                    }
                    if (removedInDraw > 0)
                        ModEntry.Log($"    [경고] 뽑기더미에 HasBeenRemovedFromState=true 카드 {removedInDraw}장!");
                }
            }
            catch { }

            // 7. NCard 노드 상태 (핸드)
            try
            {
                var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
                if (tree?.Root != null)
                {
                    var handNode = FindNodeByType(tree.Root, "NPlayerHand");
                    var container = handNode != null ? FindNodeByName(handNode, "CardHolderContainer") : null;
                    if (container != null)
                    {
                        ModEntry.Log($"  [NPlayerHand UI]");
                        ModEntry.Log($"    CardHolderContainer children = {container.GetChildCount()}");
                        for (int i = 0; i < container.GetChildCount(); i++)
                        {
                            var holder = container.GetChild(i);
                            var cmProp = holder.GetType().GetProperty("CardModel", AllInstance);
                            var cardModel = cmProp?.GetValue(holder) as CardModel;
                            ModEntry.Log($"    [{i}] {holder.GetType().Name}: CardModel={cardModel?.Id.Entry ?? "null"}");
                        }
                    }
                }
            }
            catch { }

            ModEntry.Log($"=== 진단 덤프 끝 ===\n");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"진단 덤프 실패: {ex.Message}");
        }
    }

    private static void SetProperty(object obj, string name, object value)
    {
        var prop = obj.GetType().GetProperty(name, AllInstance);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(obj, value);
            return;
        }
        // 프로퍼티에 setter가 없으면 backing field 시도
        SetField(obj, $"<{name}>k__BackingField", value);
    }

    private static void SetField(object obj, string name, object value)
    {
        var field = obj.GetType().GetField(name, AllInstance);
        if (field != null)
        {
            field.SetValue(obj, value);
        }
        else
        {
            // 대소문자 무시하고 검색
            field = obj.GetType().GetFields(AllInstance)
                .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (field != null)
                field.SetValue(obj, value);
        }
    }
}
