using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

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

    // 전투 기록 (History) 엔트리 수
    public int HistoryEntryCount;

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
        public int EnchantmentAmount;        // 인챈트 수치
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
    }

    public class PowerSnapshot
    {
        public string Id = "";
        public int Amount;
        public PowerModel? PowerRef;  // 원본 파워 참조 (죽은 적 복원용)
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

                    // CardHolderContainer에서 홀더 수집
                    var cardHolderContainer = FindNodeByName(tree.Root, "CardHolderContainer");
                    if (cardHolderContainer != null)
                    {
                        for (int i = 0; i < cardHolderContainer.GetChildCount(); i++)
                        {
                            var child = cardHolderContainer.GetChild(i);
                            if (child.GetType().Name == "NHandCardHolder")
                                snap.HandHolders.Add(child);
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
                snap.PlayerPowers.Add(new PowerSnapshot { Id = p.Id.Entry, Amount = p.Amount, PowerRef = p });
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
                    es.Powers.Add(new PowerSnapshot { Id = p.Id.Entry, Amount = p.Amount, PowerRef = p });
                }
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
                            petSnap.Powers.Add(new PowerSnapshot { Id = p.Id.Entry, Amount = p.Amount, PowerRef = p });
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

            // 유물 카운터
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
                    });
                }
            }

            // 전투 기록 엔트리 수 저장
            try
            {
                var history = CombatManager.Instance?.History;
                if (history != null)
                    snap.HistoryEntryCount = history.Entries.Count();
            }
            catch { }

            ModEntry.Log($"스냅샷 저장: HP {snap.PlayerHp}, 에너지 {snap.Energy}, 핸드 {snap.Hand.Count}장, History {snap.HistoryEntryCount}");
            return snap;
        }
        catch (Exception ex)
        {
            ModEntry.Log("스냅샷 저장 실패: " + ex.Message);
            return null;
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
            SetProperty(state, "RoundNumber", RoundNumber);

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

                SetProperty(state, "ArePlayerActionsDisabled", false);
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

            // 유물 카운터 복원
            foreach (var rs in Relics)
            {
                if (rs.RelicRef != null && rs.RelicRef.DisplayAmount != rs.Counter)
                {
                    ModEntry.Log($"  유물: {rs.Id}, 저장={rs.Counter}, 현재={rs.RelicRef.DisplayAmount}");
                    RestoreRelicCounter(rs);
                    ModEntry.Log($"    설정 후: {rs.RelicRef.DisplayAmount}");
                }
            }

            // 핸드 이외의 더미 복원 (UI 차이 계산을 위해 현재 카운트 보존)
            int preDrawCount = pcs.DrawPile.Cards.Count;
            int preDiscardCount = pcs.DiscardPile.Cards.Count;
            int preExhaustCount = pcs.ExhaustPile.Cards.Count;

            RestoreCardPile(pcs.DrawPile, DrawPile);
            RestoreCardPile(pcs.DiscardPile, DiscardPile);
            RestoreCardPile(pcs.ExhaustPile, ExhaustPile);

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

            // 플레이어 파워 복원
            RestorePowers(creature, PlayerPowers);

            // ChainsOfBindingPower의 boundCardPlayed 플래그 리셋
            // (Bound 구속 카드 사용 후 undo 시 재사용 가능하도록)
            ResetBoundCardPlayedFlag(creature);

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

            // 카드 더미 UI 버튼 직접 갱신
            RefreshPileButtons(pcs, preDrawCount, preDiscardCount, preExhaustCount);

            ModEntry.Log($"스냅샷 복원: HP {PlayerHp}, 에너지 {Energy}, 핸드 {Hand.Count}장");
            return true;
        }
        catch (Exception ex)
        {
            ModEntry.Log("스냅샷 복원 실패: " + ex.Message);
            return false;
        }
    }

    private static List<CardSnapshot> CaptureCards(CardPile? pile)
    {
        if (pile == null) return new();
        return pile.Cards.Select(c => {
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
                cs.EnchantmentAmount = c.Enchantment.Amount;
            }
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
                        var afflictInternal = cs.CardRef.GetType().GetMethod("AfflictInternal", AllInstance);
                        afflictInternal?.Invoke(cs.CardRef, new[] { mutable, (decimal)cs.AfflictionAmount });
                        ModEntry.Log($"    구속 복원: {cs.Id} ← {cs.AfflictionId}({cs.AfflictionAmount})");
                    }
                }
                else if (!hadAffliction && hasAffliction)
                {
                    // 스냅샷에 구속이 없었는데 지금 있으면 → 제거
                    var clearAffliction = cs.CardRef.GetType().GetMethod("ClearAfflictionInternal", AllInstance);
                    clearAffliction?.Invoke(cs.CardRef, null);
                    ModEntry.Log($"    구속 제거: {cs.Id} (was {currentAffliction!.Id.Entry})");
                }
                else if (hadAffliction && hasAffliction)
                {
                    // 둘 다 있지만 종류/수치가 다르면 → 교체
                    if (currentAffliction!.Id.Entry != cs.AfflictionId || currentAffliction.Amount != cs.AfflictionAmount)
                    {
                        var clearAffliction = cs.CardRef.GetType().GetMethod("ClearAfflictionInternal", AllInstance);
                        clearAffliction?.Invoke(cs.CardRef, null);

                        var mutable = CreateMutableAffliction(cs.AfflictionCategory!, cs.AfflictionId!);
                        if (mutable != null)
                        {
                            var afflictInternal = cs.CardRef.GetType().GetMethod("AfflictInternal", AllInstance);
                            afflictInternal?.Invoke(cs.CardRef, new[] { mutable, (decimal)cs.AfflictionAmount });
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

                if (hadEnchantment && !hasEnchantment)
                {
                    // 스냅샷에 인챈트가 있었는데 지금 없으면 → 복원
                    var mutable = CreateMutableEnchantment(cs.EnchantmentCategory!, cs.EnchantmentId!);
                    if (mutable != null)
                    {
                        var enchantInternal = cs.CardRef.GetType().GetMethod("EnchantInternal", AllInstance);
                        enchantInternal?.Invoke(cs.CardRef, new[] { mutable, (decimal)cs.EnchantmentAmount });
                        ModEntry.Log($"    인챈트 복원: {cs.Id} ← {cs.EnchantmentId}({cs.EnchantmentAmount})");
                    }
                }
                else if (!hadEnchantment && hasEnchantment)
                {
                    // 스냅샷에 인챈트가 없었는데 지금 있으면 → 제거
                    var clearEnchantment = cs.CardRef.GetType().GetMethod("ClearEnchantmentInternal", AllInstance);
                    clearEnchantment?.Invoke(cs.CardRef, null);
                    ModEntry.Log($"    인챈트 제거: {cs.Id} (was {currentEnchantment!.Id.Entry})");
                }
                else if (hadEnchantment && hasEnchantment)
                {
                    // 둘 다 있지만 종류/수치가 다르면 → 교체
                    if (currentEnchantment!.Id.Entry != cs.EnchantmentId || currentEnchantment.Amount != cs.EnchantmentAmount)
                    {
                        var clearEnchantment = cs.CardRef.GetType().GetMethod("ClearEnchantmentInternal", AllInstance);
                        clearEnchantment?.Invoke(cs.CardRef, null);

                        var mutable = CreateMutableEnchantment(cs.EnchantmentCategory!, cs.EnchantmentId!);
                        if (mutable != null)
                        {
                            var enchantInternal = cs.CardRef.GetType().GetMethod("EnchantInternal", AllInstance);
                            enchantInternal?.Invoke(cs.CardRef, new[] { mutable, (decimal)cs.EnchantmentAmount });
                            ModEntry.Log($"    인챈트 교체: {cs.Id} ← {cs.EnchantmentId}({cs.EnchantmentAmount})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log($"    인챈트 복원 실패 ({cs.Id}): {ex.InnerException?.Message ?? ex.Message}");
            }
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

        // 방법 1: 내부 카운트 필드 직접 설정
        var countField = btn.GetType().GetField("_count", AllInstance)
            ?? btn.GetType().GetField("_cardCount", AllInstance)
            ?? btn.GetType().GetField("count", AllInstance);
        // 부모 타입 필드도 검색
        if (countField == null)
        {
            var baseType = btn.GetType().BaseType;
            while (baseType != null && baseType != typeof(Godot.Node))
            {
                countField = baseType.GetField("_count", AllInstance)
                    ?? baseType.GetField("_cardCount", AllInstance)
                    ?? baseType.GetField("count", AllInstance);
                if (countField != null) break;
                baseType = baseType.BaseType;
            }
        }

        if (countField != null && countField.FieldType == typeof(int))
        {
            countField.SetValue(btn, targetCount);
            ModEntry.Log($"  {name} 카운트 필드 직접 설정: {targetCount}");
        }

        // 방법 2: AddCard/RemoveCard로 차이 조정
        int diff = targetCount - preCount;
        if (diff != 0)
        {
            var addCardMethod = btn.GetType().GetMethod("AddCard", AllInstance);
            var removeCardMethod = btn.GetType().GetMethod("RemoveCard", AllInstance);

            if (diff > 0 && addCardMethod != null)
                for (int i = 0; i < diff; i++) addCardMethod.Invoke(btn, null);
            else if (diff < 0 && removeCardMethod != null)
                for (int i = 0; i < -diff; i++) removeCardMethod.Invoke(btn, null);

            ModEntry.Log($"  {name} AddCard/RemoveCard: diff={diff}");
        }

        // 방법 3: Label 자식 찾아서 텍스트 직접 설정
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
            var savedPowerMap = new Dictionary<string, int>();
            foreach (var sp in savedPowers)
                savedPowerMap[sp.Id] = sp.Amount;

            // 저장 상태에 없는 파워 → RemoveInternal()로 완전 제거
            // (리스트에서 제거 + PowerRemoved 이벤트 → NPower UI 자동 정리)
            var powersToRemove = new List<PowerModel>();
            foreach (var power in powers)
            {
                var id = power.Id.Entry;
                if (savedPowerMap.TryGetValue(id, out int savedAmount))
                {
                    if (power.Amount != savedAmount)
                    {
                        SetProperty(power, "Amount", savedAmount);
                        ModEntry.Log($"    파워 수량 복원: {id} → {savedAmount}");
                    }
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

            // 스냅샷에 있지만 현재 없는 파워 → 저장된 PowerRef를 직접 _powers에 추가
            // savedPowerMap에 남아있는 항목이 추가해야 할 파워
            if (savedPowerMap.Count > 0)
            {
                // savedPowers에서 PowerRef를 찾아 사용
                var powersField = creature.GetType().GetField("_powers", AllInstance);
                var powersList = powersField?.GetValue(creature) as System.Collections.IList;

                foreach (var kvp in savedPowerMap)
                {
                    string powerId = kvp.Key;
                    int amount = kvp.Value;
                    try
                    {
                        // 저장된 PowerRef 찾기
                        var savedPower = savedPowers.FirstOrDefault(sp => sp.Id == powerId);
                        if (savedPower?.PowerRef != null)
                        {
                            // Owner를 이 creature로 설정
                            SetProperty(savedPower.PowerRef, "Owner", creature);
                            SetProperty(savedPower.PowerRef, "Amount", amount);

                            // _powers 리스트에 직접 추가
                            if (powersList != null && !powersList.Contains(savedPower.PowerRef))
                            {
                                powersList.Add(savedPower.PowerRef);
                                // PowerApplied 이벤트 발생 (NPowerContainer가 듣고 있으면 UI 갱신)
                                try
                                {
                                    creature.GetType().GetField("PowerApplied", AllInstance)?
                                        .GetValue(creature);
                                    // 이벤트 직접 호출은 복잡하므로 생략 — AddCreature가 새 노드를 만들어 처리
                                }
                                catch { }
                                ModEntry.Log($"    파워 직접 추가: {powerId} = {amount}");
                            }
                        }
                        else
                        {
                            ModEntry.Log($"    파워 추가 실패: {powerId} - PowerRef 없음");
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

            // Clear(bool notify) → AddInternal(card, index, notify) 사용
            // 게임의 공식 internal API를 호출하여 UI 이벤트가 제대로 발화되게 함
            var clearMethod = pile.GetType().GetMethod("Clear", AllInstance);
            var addMethod = pile.GetType().GetMethod("AddInternal", AllInstance);

            if (clearMethod != null && addMethod != null)
            {
                ModEntry.Log($"  CardPile 복원: 현재 {pile.Cards.Count}장 → {saved.Count}장");
                clearMethod.Invoke(pile, new object[] { false });

                for (int i = 0; i < saved.Count; i++)
                {
                    if (saved[i].CardRef != null)
                    {
                        addMethod.Invoke(pile, new object[] { saved[i].CardRef, i, false });
                    }
                }
            }
            else
            {
                ModEntry.Log($"  카드 더미 복원 실패 - Clear: {clearMethod != null}, AddInternal: {addMethod != null}");
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"카드 더미 복원 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 핸드 복원: CardPile 데이터 + NPlayerHand.Add(NCard) UI 동시 복원.
    /// </summary>
    private void RestoreHand(PlayerCombatState pcs, List<CardSnapshot> savedHand)
    {
        try
        {
            var hand = pcs.Hand;
            var clearMethod = hand.GetType().GetMethod("Clear", AllInstance);
            var addInternalMethod = hand.GetType().GetMethod("AddInternal", AllInstance);

            ModEntry.Log($"  핸드 복원: 현재 {hand.Cards.Count}장 → {savedHand.Count}장");

            // 1. CardPile 데이터 복원
            clearMethod?.Invoke(hand, new object[] { false });
            for (int i = 0; i < savedHand.Count; i++)
            {
                if (savedHand[i].CardRef != null)
                    addInternalMethod?.Invoke(hand, new object[] { savedHand[i].CardRef, i, false });
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

            // 누락된 카드 찾기 (원래 인덱스 보존)
            var missingCards = savedHand
                .Select((c, idx) => (card: c, index: idx))
                .Where(x => x.card.CardRef != null && !currentCardModels.Contains(x.card.CardRef!))
                .ToList();

            ModEntry.Log($"  현재 UI: {currentCardModels.Count}장, 제거: {holdersToRemove.Count}장, 누락: {missingCards.Count}장");

            if (missingCards.Count == 0) return;

            // NCard 씬 경로 찾기
            var existingCard = FindNodeByType(containerNode, "NCard");
            string? scenePath = existingCard?.SceneFilePath;
            var nCardType = existingCard?.GetType();

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

            // NPlayerHand.Add 메서드
            var addMethod = handNode.GetType().GetMethod("Add", AllInstance);
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

                    // 원래 인덱스에 추가 (현재 자식 수 초과 시 클램프)
                    int insertIdx = Math.Min(originalIndex, containerNode.GetChildCount());
                    addMethod.Invoke(handNode, new object[] { newNCard, insertIdx });
                    ModEntry.Log($"    카드 추가 성공: {missing.CardRef?.Id.Entry} at index {insertIdx} (원래 {originalIndex})");
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

    private static void InvokeRelicDisplayChanged(RelicModel relic)
    {
        var evt = typeof(RelicModel).GetField("DisplayAmountChanged", AllInstance);
        var handler = evt?.GetValue(relic) as Action;
        handler?.Invoke();
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
