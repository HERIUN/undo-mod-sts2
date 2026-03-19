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
    private const int MaxSnapshots = 20;

    /// <summary>현재 전투 상태를 스냅샷으로 저장</summary>
    public static void SaveSnapshot()
    {
        try
        {
            var state = CombatManager.Instance?.DebugOnlyGetState();
            if (state == null) return;

            var snap = StateSnapshot.Capture(state);
            if (snap == null) return;

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
        if (_snapshots.Count == 0)
        {
            ModEntry.Log("되돌릴 스냅샷이 없습니다.");
            return false;
        }

        var state = CombatManager.Instance?.DebugOnlyGetState();
        if (state == null)
        {
            ModEntry.Log("전투 상태를 가져올 수 없습니다.");
            return false;
        }

        // 카드 선택 UI가 열려있으면 undo 차단 (게임 액션 파이프라인 깨짐 방지)
        try
        {
            var tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            if (tree?.Root != null)
            {
                var handNode = FindNodeByType(tree.Root, "NPlayerHand");
                if (handNode != null)
                {
                    var isInSelectionProp = handNode.GetType().GetProperty("IsInCardSelection",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    var isInSelection = isInSelectionProp?.GetValue(handNode) as bool? ?? false;
                    if (isInSelection)
                    {
                        ModEntry.Log("카드 선택 중에는 Undo 불가 — 선택을 완료하거나 취소하세요.");
                        return false;
                    }
                }
            }
        }
        catch { }

        var snap = _snapshots.Pop();
        bool ok = snap.Restore(state);
        if (ok)
            ModEntry.Log($"Undo 완료! (남은 스냅샷: {_snapshots.Count})");
        return ok;
    }

    private static Godot.Node? FindNodeByType(Godot.Node root, string typeName)
    {
        if (root.GetType().Name == typeName) return root;
        for (int i = 0; i < root.GetChildCount(); i++)
        {
            var found = FindNodeByType(root.GetChild(i), typeName);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>전투 시작/종료 시 스냅샷 초기화</summary>
    public static void Clear()
    {
        _snapshots.Clear();
        ModEntry.Log("스냅샷 스택 초기화");
    }

    public static int Count => _snapshots.Count;
}
