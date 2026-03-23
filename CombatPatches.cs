using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;

namespace UndoMod;

[HarmonyPatch]
public static class CombatPatches
{
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
    [HarmonyPostfix]
    public static void AfterSetUpCombat()
    {
        UndoManager.Clear();
        PlayCardPatcher.Patch();
        UsePotionPatcher.Patch();
        EndTurnPatcher.Patch();
        TurnDiagnosticPatcher.Patch();
        ReplayWriterPatcher.Patch();
        NCardSafetyPatcher.Patch();
        ModEntry.Log("전투 시작 - Undo 준비 완료");
    }
}

/// <summary>
/// 턴 전환 관련 메서드에 진단 로그를 추가하는 패처.
/// StartTurn, SetupPlayerTurn, EndPlayerTurn, SetReadyToEndTurn, Draw 등
/// </summary>
public static class TurnDiagnosticPatcher
{
    private static bool _patched;
    private static readonly Harmony _harmony = new("undo_mod.turndiag");

    public static void Patch()
    {
        if (_patched) return;
        _patched = true;

        try
        {
            var asm = typeof(CombatManager).Assembly;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var cmType = typeof(CombatManager);

            // CombatManager.CheckWinCondition
            PatchMethod(cmType, "CheckWinCondition", nameof(BeforeCheckWinCondition), null);

            // CombatManager.IsEnding getter - 정적 플래그로 1회만 로깅
            try
            {
                var isEndingProp = cmType.GetProperty("IsEnding", flags);
                if (isEndingProp?.GetMethod != null)
                {
                    var prefix = typeof(TurnDiagnosticPatcher).GetMethod(nameof(BeforeIsEnding),
                        BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(isEndingProp.GetMethod, prefix: new HarmonyMethod(prefix));
                    ModEntry.Log("  진단패치: CombatManager.IsEnding getter");
                }
            }
            catch (Exception ex) { ModEntry.Log($"  IsEnding 패치 실패: {ex.Message}"); }

            // CreatureCmd.Kill (오버로드가 있으므로 파라미터로 구분)
            try
            {
                var creatureCmdType = asm.GetType("MegaCrit.Sts2.Core.Commands.CreatureCmd");
                if (creatureCmdType != null)
                {
                    var killMethods = creatureCmdType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .Where(m => m.Name == "Kill").ToArray();
                    foreach (var km in killMethods)
                    {
                        var parms = km.GetParameters();
                        ModEntry.Log($"  Kill 오버로드: ({string.Join(", ", parms.Select(p => p.ParameterType.Name))})");
                    }
                    // IReadOnlyCollection<Creature> 파라미터를 가진 오버로드 패치
                    var killMethod = killMethods.FirstOrDefault(m =>
                        m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType.Name.Contains("IReadOnly"));
                    if (killMethod != null)
                    {
                        var prefix = typeof(TurnDiagnosticPatcher).GetMethod(nameof(BeforeCreatureKill),
                            BindingFlags.Static | BindingFlags.Public);
                        _harmony.Patch(killMethod, prefix: new HarmonyMethod(prefix));
                        ModEntry.Log("  진단패치: CreatureCmd.Kill");
                    }
                }
            }
            catch (Exception ex) { ModEntry.Log($"  Kill 패치 실패 (무시): {ex.Message}"); }

            // CombatManager.StartTurn
            PatchMethod(cmType, "StartTurn", nameof(BeforeStartTurn), nameof(AfterStartTurn));

            // CombatManager.SetupPlayerTurn
            PatchMethod(cmType, "SetupPlayerTurn", nameof(BeforeSetupPlayerTurn), nameof(AfterSetupPlayerTurn));

            // CombatManager.SetReadyToEndTurn
            PatchMethod(cmType, "SetReadyToEndTurn", nameof(BeforeSetReadyToEndTurn), null);

            // CombatManager.AfterAllPlayersReadyToEndTurn
            PatchMethod(cmType, "AfterAllPlayersReadyToEndTurn", nameof(BeforeAfterAllPlayersReady), null);

            // EndPlayerTurnAction.ExecuteAction
            var endTurnActionType = asm.GetType("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            if (endTurnActionType != null)
                PatchMethod(endTurnActionType, "ExecuteAction", nameof(BeforeEndPlayerTurnAction), nameof(AfterEndPlayerTurnAction));

            // CardPileCmd.Draw
            var cardPileCmdType = asm.GetType("MegaCrit.Sts2.Core.Entities.Cards.CardPileCmd");
            if (cardPileCmdType != null)
                PatchMethod(cardPileCmdType, "Draw", nameof(BeforeDraw), nameof(AfterDraw));

            // ActionQueueSynchronizer.SetCombatState
            var aqsType = asm.GetType("MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer");
            if (aqsType != null)
                PatchMethod(aqsType, "SetCombatState", nameof(BeforeSetCombatState), null);

            ModEntry.Log("턴 진단 패치 완료");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"턴 진단 패치 실패: {ex.Message}");
        }
    }

    private static void PatchMethod(Type type, string methodName, string? prefixName, string? postfixName)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = type.GetMethod(methodName, flags);
            if (method == null)
            {
                ModEntry.Log($"  진단패치 대상 없음: {type.Name}.{methodName}");
                return;
            }

            var myType = typeof(TurnDiagnosticPatcher);
            var prefix = prefixName != null ? myType.GetMethod(prefixName, BindingFlags.Static | BindingFlags.Public) : null;
            var postfix = postfixName != null ? myType.GetMethod(postfixName, BindingFlags.Static | BindingFlags.Public) : null;

            _harmony.Patch(method,
                prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                postfix: postfix != null ? new HarmonyMethod(postfix) : null);
            ModEntry.Log($"  진단패치: {type.Name}.{methodName}");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"  진단패치 실패 {type.Name}.{methodName}: {ex.Message}");
        }
    }

    // === StartTurn ===
    public static void BeforeStartTurn(object __instance)
    {
        try
        {
            var cm = __instance as CombatManager;
            var state = cm?.DebugOnlyGetState();
            ModEntry.Log($"[진단] StartTurn 진입: RoundNumber={state?.RoundNumber}, IsPlayPhase={GetProp(__instance, "IsPlayPhase")}");
        }
        catch (Exception ex) { ModEntry.Log($"[진단] StartTurn prefix 오류: {ex.Message}"); }
    }

    public static void AfterStartTurn(object __instance)
    {
        try
        {
            var cm = __instance as CombatManager;
            var state = cm?.DebugOnlyGetState();
            ModEntry.Log($"[진단] StartTurn 완료: RoundNumber={state?.RoundNumber}, IsPlayPhase={GetProp(__instance, "IsPlayPhase")}");
        }
        catch (Exception ex) { ModEntry.Log($"[진단] StartTurn postfix 오류: {ex.Message}"); }
    }

    // === SetupPlayerTurn ===
    public static void BeforeSetupPlayerTurn(object __instance)
    {
        try
        {
            ModEntry.Log($"[진단] SetupPlayerTurn 진입: PlayerActionsDisabled={GetProp(__instance, "PlayerActionsDisabled")}");
            // 현재 스택 트레이스
            ModEntry.Log($"[진단] SetupPlayerTurn 호출 스택:\n{Environment.StackTrace}");
        }
        catch (Exception ex) { ModEntry.Log($"[진단] SetupPlayerTurn prefix 오류: {ex.Message}"); }
    }

    public static void AfterSetupPlayerTurn(object __instance)
    {
        try
        {
            var cm = __instance as CombatManager;
            var state = cm?.DebugOnlyGetState();
            var player = state?.Players.FirstOrDefault();
            var pcs = player?.PlayerCombatState;
            ModEntry.Log($"[진단] SetupPlayerTurn 완료: Hand={pcs?.Hand.Cards.Count}, Energy={pcs?.Energy}, PlayerActionsDisabled={GetProp(__instance, "PlayerActionsDisabled")}");
        }
        catch (Exception ex) { ModEntry.Log($"[진단] SetupPlayerTurn postfix 오류: {ex.Message}"); }
    }

    // === SetReadyToEndTurn ===
    public static void BeforeSetReadyToEndTurn(object __instance)
    {
        try
        {
            var cm = __instance as CombatManager;
            var state = cm?.DebugOnlyGetState();
            ModEntry.Log($"[진단] SetReadyToEndTurn: RoundNumber={state?.RoundNumber}, IsPlayPhase={GetProp(__instance, "IsPlayPhase")}");
            ModEntry.Log($"[진단] SetReadyToEndTurn 호출 스택:\n{Environment.StackTrace}");
        }
        catch (Exception ex) { ModEntry.Log($"[진단] SetReadyToEndTurn prefix 오류: {ex.Message}"); }
    }

    // === AfterAllPlayersReadyToEndTurn ===
    public static void BeforeAfterAllPlayersReady(object __instance)
    {
        try
        {
            var cm = __instance as CombatManager;
            var state = cm?.DebugOnlyGetState();
            ModEntry.Log($"[진단] AfterAllPlayersReadyToEndTurn: RoundNumber={state?.RoundNumber}");
        }
        catch (Exception ex) { ModEntry.Log($"[진단] AfterAllPlayersReady 오류: {ex.Message}"); }
    }

    // === EndPlayerTurnAction ===
    public static void BeforeEndPlayerTurnAction(object __instance)
    {
        try
        {
            // _combatRound 필드 읽기
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var roundField = __instance.GetType().GetField("_combatRound", flags);
            var savedRound = roundField?.GetValue(__instance);
            var state = CombatManager.Instance?.DebugOnlyGetState();
            ModEntry.Log($"[진단] EndPlayerTurnAction: _combatRound={savedRound}, 현재RoundNumber={state?.RoundNumber}");
            if (savedRound != null && state != null && !savedRound.Equals(state.RoundNumber))
            {
                ModEntry.Log($"[진단] *** EndPlayerTurnAction 라운드 불일치! 스킵될 수 있음 ***");
            }
        }
        catch (Exception ex) { ModEntry.Log($"[진단] EndPlayerTurnAction prefix 오류: {ex.Message}"); }
    }

    public static void AfterEndPlayerTurnAction(object __instance)
    {
        try
        {
            ModEntry.Log($"[진단] EndPlayerTurnAction 완료");
        }
        catch { }
    }

    // === CardPileCmd.Draw ===
    public static void BeforeDraw(object __instance)
    {
        try
        {
            ModEntry.Log($"[진단] CardPileCmd.Draw 호출");
        }
        catch { }
    }

    public static void AfterDraw(object __instance)
    {
        try
        {
            // 드로우 후 예외가 발생했는지 확인
            ModEntry.Log($"[진단] CardPileCmd.Draw 완료 (정상)");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"[진단] CardPileCmd.Draw postfix 예외: {ex.Message}");
        }
    }

    // === ActionQueueSynchronizer.SetCombatState ===
    public static void BeforeSetCombatState(object __instance, object __0)
    {
        try
        {
            var currentProp = __instance.GetType().GetProperty("CombatState",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var current = currentProp?.GetValue(__instance);
            ModEntry.Log($"[진단] AQS.SetCombatState: {current} → {__0}");
        }
        catch { }
    }

    // === CheckWinCondition ===
    public static void BeforeCheckWinCondition(object __instance)
    {
        try
        {
            var cm = __instance as CombatManager;
            var isEnding = GetProp(__instance, "IsEnding");
            var isInProgress = GetProp(__instance, "IsInProgress");
            ModEntry.Log($"[진단] CheckWinCondition: IsEnding={isEnding}, IsInProgress={isInProgress}");
        }
        catch { }
    }

    // === IsEnding ===
    private static bool _isEndingLogged;
    public static void BeforeIsEnding(object __instance, ref bool __result)
    {
        // 너무 자주 호출되므로 적이 죽었을 때만 로깅
        try
        {
            var cm = __instance as CombatManager;
            var state = cm?.DebugOnlyGetState();
            if (state == null) return;
            bool anyDead = state.Enemies.Any(e => e.IsDead);
            if (anyDead && !_isEndingLogged)
            {
                _isEndingLogged = true;
                ModEntry.Log($"[진단] IsEnding 체크 (적 사망 감지):");
                ModEntry.Log($"  IsInProgress={GetProp(__instance, "IsInProgress")}");
                foreach (var e in state.Enemies)
                {
                    var isPrimary = e.GetType().GetProperty("IsPrimaryEnemy",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(e);
                    ModEntry.Log($"  적: {e.Name}, IsAlive={e.IsAlive}, IsDead={e.IsDead}, HP={e.CurrentHp}, IsPrimary={isPrimary}");
                }
            }
        }
        catch { }
    }

    // === CreatureCmd.Kill ===
    public static void BeforeCreatureKill(object __0)
    {
        try
        {
            if (__0 is System.Collections.IEnumerable creatures)
            {
                foreach (var c in creatures)
                {
                    if (c is MegaCrit.Sts2.Core.Entities.Creatures.Creature creature)
                        ModEntry.Log($"[진단] CreatureCmd.Kill: {creature.Name}, HP={creature.CurrentHp}, IsAlive={creature.IsAlive}");
                }
            }
        }
        catch (Exception ex) { ModEntry.Log($"[진단] Kill 로그 실패: {ex.Message}"); }
    }

    private static object? GetProp(object obj, string name)
    {
        try
        {
            return obj.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);
        }
        catch { return "error"; }
    }
}

/// <summary>
/// PlayCardAction.ExecuteAction()에 prefix를 패치하여
/// 카드 사용 직전에 스냅샷을 저장한다.
/// </summary>
public static class PlayCardPatcher
{
    private static bool _patched;
    private static readonly Harmony _harmony = new("undo_mod.playcard");

    public static void Patch()
    {
        if (_patched) return;

        try
        {
            // PlayCardAction 타입 찾기
            var playCardType = typeof(CombatManager).Assembly.GetType(
                "MegaCrit.Sts2.Core.GameActions.PlayCardAction");

            if (playCardType == null)
            {
                ModEntry.Log("PlayCardAction 타입을 찾을 수 없습니다.");
                return;
            }

            // ExecuteAction 메서드 패치 (override된 구현)
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var executeAction = playCardType.GetMethod("ExecuteAction", flags);

            if (executeAction == null)
            {
                ModEntry.Log("PlayCardAction.ExecuteAction 메서드를 찾을 수 없습니다.");
                // fallback: 모든 메서드 출력
                foreach (var m in playCardType.GetMethods(flags).Where(m => !m.IsSpecialName))
                {
                    ModEntry.Log($"  {m.Name} (DeclaringType: {m.DeclaringType?.Name})");
                }
                return;
            }

            var prefix = typeof(PlayCardPatcher).GetMethod(nameof(BeforePlayCard),
                BindingFlags.Static | BindingFlags.Public);
            _harmony.Patch(executeAction, prefix: new HarmonyMethod(prefix));
            _patched = true;
            ModEntry.Log("PlayCardAction.ExecuteAction 패치 완료!");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"PlayCardAction 패치 오류: {ex.Message}");
        }
    }

    public static void BeforePlayCard()
    {
        if (!UndoManager.IsRestoring)
            UndoManager.SaveSnapshot();
    }
}

/// <summary>
/// EndPlayerTurnAction 생성자에 prefix를 패치하여
/// 턴 종료 직전에 스냅샷을 저장한다.
/// </summary>
public static class EndTurnPatcher
{
    private static bool _patched;
    private static readonly Harmony _harmony = new("undo_mod.endturn");

    public static void Patch()
    {
        if (_patched) return;

        try
        {
            var asm = typeof(CombatManager).Assembly;
            var endTurnType = asm.GetType("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");

            if (endTurnType == null)
            {
                ModEntry.Log("EndPlayerTurnAction 타입을 찾을 수 없습니다.");
                return;
            }

            // 생성자에 패치 (참고 레포 방식: 생성자 prefix)
            var ctors = endTurnType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ctors.Length > 0)
            {
                var prefix = typeof(EndTurnPatcher).GetMethod(nameof(BeforeEndTurn),
                    BindingFlags.Static | BindingFlags.Public);
                _harmony.Patch(ctors[0], prefix: new HarmonyMethod(prefix));
                _patched = true;
                ModEntry.Log("EndPlayerTurnAction 생성자 패치 완료!");
            }
            else
            {
                // fallback: ExecuteAction에 패치
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var executeAction = endTurnType.GetMethod("ExecuteAction", flags);
                if (executeAction != null)
                {
                    var prefix = typeof(EndTurnPatcher).GetMethod(nameof(BeforeEndTurn),
                        BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(executeAction, prefix: new HarmonyMethod(prefix));
                    _patched = true;
                    ModEntry.Log("EndPlayerTurnAction.ExecuteAction 패치 완료!");
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log($"EndPlayerTurnAction 패치 오류: {ex.Message}");
        }
    }

    public static void BeforeEndTurn()
    {
        if (!UndoManager.IsRestoring)
            UndoManager.SaveSnapshot();
    }
}

/// <summary>
/// NetUsePotionAction.ExecuteAction()에 prefix를 패치하여
/// 포션 사용 직전에 스냅샷을 저장한다.
/// </summary>
public static class UsePotionPatcher
{
    private static bool _patched;
    private static readonly Harmony _harmony = new("undo_mod.usepotion");

    public static void Patch()
    {
        if (_patched) return;

        try
        {
            var asm = typeof(CombatManager).Assembly;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // UsePotion 관련 GameAction 타입 검색
            Type[]? allTypes = null;
            try { allTypes = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { allTypes = ex.Types.Where(t => t != null).ToArray()!; }

            // UsePotionAction, UsePotionGameAction 등 ExecuteAction을 가진 타입 찾기
            foreach (var t in allTypes)
            {
                var tn = t.Name.ToLower();
                if (tn.Contains("potion") && (tn.Contains("use") || tn.Contains("discard")))
                {
                    var executeAction = t.GetMethod("ExecuteAction", flags);
                    if (executeAction != null)
                    {
                        var prefix = typeof(UsePotionPatcher).GetMethod(nameof(BeforeUsePotion),
                            BindingFlags.Static | BindingFlags.Public);
                        _harmony.Patch(executeAction, prefix: new HarmonyMethod(prefix));
                        ModEntry.Log($"포션 액션 패치 완료: {t.FullName}");
                    }
                    else
                    {
                        ModEntry.Log($"포션 관련 타입 (ExecuteAction 없음): {t.FullName}");
                        foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName && m.DeclaringType == t))
                            ModEntry.Log($"  M: {m.Name}");
                    }
                }
            }

            _patched = true;
        }
        catch (Exception ex)
        {
            ModEntry.Log($"포션 액션 패치 오류: {ex.Message}");
        }
    }

    public static void BeforeUsePotion()
    {
        if (!UndoManager.IsRestoring)
            UndoManager.SaveSnapshot();
    }
}

/// <summary>
/// RunManager.WriteReplay() 직렬화 오류를 무시하여 전투 종료 플로우가 크래시되지 않도록 함.
/// TOADPOLES_NORMAL 등 ModelId 매핑 실패 시 리플레이 저장만 스킵.
/// </summary>
public static class ReplayWriterPatcher
{
    private static bool _patched;
    private static readonly Harmony _harmony = new("undo_mod.replaywriter");

    public static void Patch()
    {
        if (_patched) return;
        _patched = true;

        try
        {
            var asm = typeof(CombatManager).Assembly;
            var runMgrType = asm.GetType("MegaCrit.Sts2.Core.Runs.RunManager");
            if (runMgrType == null)
            {
                ModEntry.Log("RunManager 타입 없음 - 리플레이 패치 스킵");
                return;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var writeReplay = runMgrType.GetMethod("WriteReplay", flags);
            if (writeReplay == null)
            {
                ModEntry.Log("WriteReplay 메서드 없음");
                return;
            }

            var finalizer = typeof(ReplayWriterPatcher).GetMethod(nameof(WriteReplayFinalizer),
                BindingFlags.Static | BindingFlags.Public);
            _harmony.Patch(writeReplay, finalizer: new HarmonyMethod(finalizer));
            ModEntry.Log("WriteReplay 패치 완료 (직렬화 오류 무시)");
        }
        catch (Exception ex)
        {
            ModEntry.Log($"WriteReplay 패치 오류: {ex.Message}");
        }
    }

    /// <summary>Harmony Finalizer: 예외를 먹어서 호출자로 전파되지 않게 함</summary>
    public static Exception? WriteReplayFinalizer(Exception? __exception)
    {
        if (__exception != null)
        {
            ModEntry.Log($"WriteReplay 오류 무시: {__exception.GetType().Name}: {__exception.Message}");
        }
        return null; // 예외를 null로 반환하면 Harmony가 예외를 삼킴
    }
}

/// <summary>
/// ObjectDisposedException 방지를 위한 다층 안전망.
/// 1) NCard.OnAfflictionChanged / OnEnchantmentChanged — IsInstanceValid prefix
/// 2) CardModel.AfflictInternal 등 — ObjectDisposedException finalizer
/// 3) CardCmd.Afflict / CardCmd.AfflictAndPreview — ObjectDisposedException finalizer
/// 4) CardPileCmd.Draw — ObjectDisposedException finalizer (최후 방어선)
/// </summary>
public static class NCardSafetyPatcher
{
    private static bool _patched;

    public static void Patch()
    {
        if (_patched) return;
        _patched = true;

        ModEntry.Log("[NCardSafety] 패치 시작...");

        var harmony = new Harmony("undo_mod.ncard_safety");
        var asm = typeof(CombatManager).Assembly;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static;
        int total = 0;

        // === Layer 1: NCard 이벤트 핸들러에 IsInstanceValid prefix ===
        try
        {
            var ncardType = asm.GetType("MegaCrit.Sts2.Core.Nodes.Cards.NCard");
            if (ncardType != null)
            {
                ModEntry.Log($"[NCardSafety] NCard 타입: {ncardType.FullName}");
                string[] targetMethods = { "OnAfflictionChanged", "OnEnchantmentChanged", "OnEnchantmentStatusChanged" };
                foreach (var mn in targetMethods)
                {
                    try
                    {
                        var method = ncardType.GetMethod(mn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (method != null)
                        {
                            harmony.Patch(method,
                                prefix: new HarmonyMethod(typeof(NCardSafetyPatcher), nameof(CheckInstanceValid)));
                            total++;
                            ModEntry.Log($"[NCardSafety] NCard prefix: {mn}");
                        }
                        else
                        {
                            ModEntry.Log($"[NCardSafety] NCard 메서드 없음: {mn}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Log($"[NCardSafety] NCard.{mn} 패치 실패: {ex.Message}");
                    }
                }
            }
            else
            {
                ModEntry.Log("[NCardSafety] NCard 타입 못 찾음 (Core 어셈블리)");
                // 다른 어셈블리 검색
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = a.GetType("MegaCrit.Sts2.Core.Nodes.Cards.NCard");
                        if (t != null)
                        {
                            ModEntry.Log($"[NCardSafety] NCard 발견 (다른 어셈블리): {a.GetName().Name}");
                            string[] targetMethods2 = { "OnAfflictionChanged", "OnEnchantmentChanged", "OnEnchantmentStatusChanged" };
                            foreach (var mn in targetMethods2)
                            {
                                try
                                {
                                    var method = t.GetMethod(mn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (method != null)
                                    {
                                        harmony.Patch(method,
                                            prefix: new HarmonyMethod(typeof(NCardSafetyPatcher), nameof(CheckInstanceValid)));
                                        total++;
                                        ModEntry.Log($"[NCardSafety] NCard prefix: {mn}");
                                    }
                                }
                                catch (Exception ex) { ModEntry.Log($"[NCardSafety] NCard.{mn} 패치 실패: {ex.Message}"); }
                            }
                            break;
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { ModEntry.Log($"[NCardSafety] Layer1 오류: {ex.Message}"); }

        // === Layer 2: CardModel.AfflictInternal 등에 Finalizer ===
        try
        {
            var cardModelType = asm.GetType("MegaCrit.Sts2.Core.Entities.Cards.CardModel");
            if (cardModelType != null)
            {
                foreach (var method in cardModelType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var n = method.Name;
                    if ((n.Contains("Afflict") || n.Contains("Enchant")) && method.DeclaringType == cardModelType)
                    {
                        try
                        {
                            harmony.Patch(method,
                                finalizer: new HarmonyMethod(typeof(NCardSafetyPatcher), nameof(SuppressDisposed)));
                            total++;
                            ModEntry.Log($"[NCardSafety] CardModel finalizer: {n}");
                        }
                        catch { }
                    }
                }
            }
            else { ModEntry.Log("[NCardSafety] CardModel 타입 없음"); }
        }
        catch (Exception ex) { ModEntry.Log($"[NCardSafety] Layer2 오류: {ex.Message}"); }

        // === Layer 3: CardCmd.Afflict 등에 Finalizer ===
        try
        {
            var cardCmdType = asm.GetType("MegaCrit.Sts2.Core.Commands.CardCmd");
            if (cardCmdType != null)
            {
                foreach (var method in cardCmdType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var n = method.Name;
                    if (n.Contains("Afflict") || n.Contains("Enchant"))
                    {
                        try
                        {
                            harmony.Patch(method,
                                finalizer: new HarmonyMethod(typeof(NCardSafetyPatcher), nameof(SuppressDisposed)));
                            total++;
                            ModEntry.Log($"[NCardSafety] CardCmd finalizer: {n}");
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex) { ModEntry.Log($"[NCardSafety] Layer3 오류: {ex.Message}"); }

        // === Layer 4: CardPileCmd.Draw에 Finalizer (최후 방어선) ===
        try
        {
            var cardPileCmdType = asm.GetType("MegaCrit.Sts2.Core.Entities.Cards.CardPileCmd");
            if (cardPileCmdType != null)
            {
                var drawMethod = cardPileCmdType.GetMethod("Draw",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (drawMethod != null)
                {
                    harmony.Patch(drawMethod,
                        finalizer: new HarmonyMethod(typeof(NCardSafetyPatcher), nameof(SuppressDisposed)));
                    total++;
                    ModEntry.Log("[NCardSafety] CardPileCmd.Draw finalizer");
                }
            }
        }
        catch (Exception ex) { ModEntry.Log($"[NCardSafety] Layer4 오류: {ex.Message}"); }

        // === Layer 5: CombatManager.SetupPlayerTurn에 Finalizer (절대 방어선) ===
        try
        {
            var setupMethod = typeof(CombatManager).GetMethod("SetupPlayerTurn",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (setupMethod != null)
            {
                harmony.Patch(setupMethod,
                    finalizer: new HarmonyMethod(typeof(NCardSafetyPatcher), nameof(SuppressDisposed)));
                total++;
                ModEntry.Log("[NCardSafety] CombatManager.SetupPlayerTurn finalizer");
            }
        }
        catch (Exception ex) { ModEntry.Log($"[NCardSafety] Layer5 오류: {ex.Message}"); }

        ModEntry.Log($"[NCardSafety] 패치 완료: {total}개");
    }

    /// <summary>
    /// Harmony Prefix: disposed된 Godot 노드이면 실행 스킵
    /// </summary>
    public static bool CheckInstanceValid(object __instance)
    {
        try
        {
            if (__instance is Godot.GodotObject gobj && !Godot.GodotObject.IsInstanceValid(gobj))
                return false;
        }
        catch { return false; }
        return true;
    }

    /// <summary>
    /// Harmony Finalizer: ObjectDisposedException만 삼킨다.
    /// </summary>
    public static Exception? SuppressDisposed(Exception? __exception)
    {
        if (__exception is ObjectDisposedException ode)
        {
            ModEntry.Log($"[NCardSafety] ObjectDisposedException 억제: {ode.Message}");
            return null;
        }
        return __exception;
    }
}
