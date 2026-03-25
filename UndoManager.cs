using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;

namespace UndoMod;

/// <summary>
/// Undo 스냅샷 스택 관리.
/// 카드 사용/포션 사용 직전에 스냅샷을 저장하고,
/// Undo 요청 시 마지막 스냅샷을 복원한다.
/// </summary>
public static class UndoManager
{
    private static readonly Stack<StateSnapshot> _snapshots = new();
    private const int MaxSnapshots = 100;

    /// <summary>복원 중 재귀 스냅샷 방지 플래그</summary>
    public static bool IsRestoring { get; internal set; }

    /// <summary>현재 전투 상태를 스냅샷으로 저장</summary>
    public static void SaveSnapshot()
    {
        if (IsRestoring) return;

        try
        {
            var snap = StateSnapshot.Capture();
            if (snap == null || snap.IsFailed) return;

        _snapshots.Push(snap);

        // 메모리 제한
        if (_snapshots.Count > MaxSnapshots)
        {
            var temp = new Stack<StateSnapshot>();
            int count = 0;
            foreach (var s in _snapshots)
            {
                if (count++ >= MaxSnapshots) break;
                temp.Push(s);
            }
            _snapshots.Clear();
            foreach (var s in temp)
                _snapshots.Push(s);
        }
        }
        catch (System.Exception ex)
        {
            ModEntry.Log($"스냅샷 저장 중 예외 (무시): {ex.Message}");
        }
    }

    /// <summary>마지막 스냅샷으로 복원</summary>
    public static bool Undo()
    {
        if (IsRestoring)
        {
            ModEntry.Log("복원 중에는 Undo 불가합니다.");
            return false;
        }

        if (_snapshots.Count == 0)
        {
            ModEntry.Log("되돌릴 스냅샷이 없습니다.");
            return false;
        }

        if (CombatManager.Instance == null)
        {
            ModEntry.Log("전투 상태를 가져올 수 없습니다.");
            return false;
        }

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

        // === 가드 체크 ===

        // 1) ActionQueueSet.IsEmpty — 진행 중인 액션이 있으면 차단
        try
        {
            var runInstance = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
            if (runInstance != null)
            {
                var aqSetProp = runInstance.GetType().GetProperty("ActionQueueSet", flags);
                var aqSet = aqSetProp?.GetValue(runInstance);
                if (aqSet != null)
                {
                    var isEmptyProp = aqSet.GetType().GetProperty("IsEmpty", flags);
                    var isEmpty = isEmptyProp?.GetValue(aqSet) as bool? ?? true;
                    if (!isEmpty)
                    {
                        ModEntry.Log("액션 실행 중에는 Undo 불가 — 액션 완료를 기다리세요.");
                        return false;
                    }
                }
            }
        }
        catch { }

        // 2) 카드 선택 UI가 열려있으면 undo 차단
        try
        {
            var hand = MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance;
            if (hand != null)
            {
                var isInSelectionProp = hand.GetType().GetProperty("IsInCardSelection", flags);
                var isInSelection = isInSelectionProp?.GetValue(hand) as bool? ?? false;
                if (isInSelection)
                {
                    ModEntry.Log("카드 선택 중에는 Undo 불가 — 선택을 완료하거나 취소하세요.");
                    return false;
                }
            }
        }
        catch { }

        var snap = _snapshots.Pop();

        IsRestoring = true;
        bool ok;
        try
        {
            ok = snap.Restore();
        }
        finally
        {
            IsRestoring = false;
        }

        if (ok)
        {
            // 데이터 복원 후 비주얼 갱신 (참조 레포 패턴)
            try { VisualRefresh.RefreshAllVisuals(); }
            catch (System.Exception ex) { ModEntry.Log($"RefreshAllVisuals 실패: {ex.Message}"); }

            ModEntry.Log($"Undo 완료! (남은 스냅샷: {_snapshots.Count})");
        }
        return ok;
    }

    /// <summary>전투 시작/종료 시 스냅샷 초기화</summary>
    public static void Clear()
    {
        _snapshots.Clear();
        ModEntry.Log("스냅샷 스택 초기화");
    }

    public static int Count => _snapshots.Count;
}
