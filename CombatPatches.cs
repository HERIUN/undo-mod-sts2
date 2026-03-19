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
        ModEntry.Log("전투 시작 - Undo 준비 완료");
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
        UndoManager.SaveSnapshot();
    }
}
