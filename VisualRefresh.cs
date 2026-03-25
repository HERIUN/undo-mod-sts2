using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace UndoMod;

/// <summary>
/// Undo 후 비주얼 갱신 — 참조 레포(UndoAndRedo)의 RefreshAllVisuals 패턴 구현.
/// 데이터 복원(StateSnapshot.Restore) 완료 후 호출.
/// </summary>
public static class VisualRefresh
{
    // ── CombatManager state access ──
    private static readonly FieldInfo? CombatManagerStateField =
        AccessTools.Field(typeof(CombatManager), "_state");

    // ── NotifyCombatStateChanged ──
    private static readonly MethodInfo? NotifyCombatStateChangedMethod =
        AccessTools.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged");

    // ── End turn state reset — CombatManager ──
    private static readonly FieldInfo? PlayersReadyToEndTurnField =
        AccessTools.Field(typeof(CombatManager), "_playersReadyToEndTurn");
    private static readonly PropertyInfo? PlayerActionsDisabledProp =
        AccessTools.Property(typeof(CombatManager), "PlayerActionsDisabled");
    private static readonly PropertyInfo? IsPlayPhaseProp =
        AccessTools.Property(typeof(CombatManager), "IsPlayPhase");
    private static readonly PropertyInfo? EndingPhaseOneProp =
        AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseOne");
    private static readonly PropertyInfo? EndingPhaseTwoProp =
        AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseTwo");
    private static readonly PropertyInfo? IsEnemyTurnStartedProp =
        AccessTools.Property(typeof(CombatManager), "IsEnemyTurnStarted");
    private static readonly FieldInfo? PlayersReadyToBeginEnemyTurnField =
        AccessTools.Field(typeof(CombatManager), "_playersReadyToBeginEnemyTurn");

    // ── NPlayerHand state ──
    private static readonly FieldInfo? HandCurrentCardPlayField =
        AccessTools.Field(typeof(NPlayerHand), "_currentCardPlay");
    private static readonly FieldInfo? HandCurrentModeField =
        AccessTools.Field(typeof(NPlayerHand), "_currentMode");
    private static readonly FieldInfo? HandDraggedHolderIndexField =
        AccessTools.Field(typeof(NPlayerHand), "_draggedHolderIndex");
    private static readonly FieldInfo? HandHoldersAwaitingQueueField =
        AccessTools.Field(typeof(NPlayerHand), "_holdersAwaitingQueue");
    private static readonly FieldInfo? HandIsDisabledField =
        AccessTools.Field(typeof(NPlayerHand), "_isDisabled");

    // ── Card holder animation snap ──
    private static Type? _holderType;
    private static FieldInfo? HolderTargetPosField;
    private static FieldInfo? HolderPosCancelField;
    private static FieldInfo? HolderTargetAngleField;
    private static FieldInfo? HolderTargetScaleField;
    private static MethodInfo? SetAngleInstantlyMethod;
    private static MethodInfo? SetScaleInstantlyMethod;

    // ── NCardPlayQueue ──
    private static readonly FieldInfo? PlayQueueField =
        AccessTools.Field(typeof(NCardPlayQueue), "_playQueue");

    // ── Power visual refresh ──
    private static readonly Type? NPowerContainerType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NPowerContainer");
    private static readonly Type? NPowerType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NPower");
    private static readonly Type? NCreatureStateDisplayType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay");
    private static readonly FieldInfo? StateDisplayPowerContainerField =
        NCreatureStateDisplayType != null ? AccessTools.Field(NCreatureStateDisplayType, "_powerContainer") : null;
    private static readonly FieldInfo? PowerContainerNodesField =
        NPowerContainerType != null ? AccessTools.Field(NPowerContainerType, "_powerNodes") : null;
    private static readonly MethodInfo? PowerContainerAddMethod =
        NPowerContainerType != null ? AccessTools.Method(NPowerContainerType, "Add",
            new[] { typeof(PowerModel) }) : null;

    // ── Creature visual snap ──
    private static readonly FieldInfo? NCreatureIntentFadeTweenField =
        AccessTools.Field(typeof(NCreature), "_intentFadeTween");
    private static readonly FieldInfo? StateDisplayShowHideTweenField =
        NCreatureStateDisplayType != null ? AccessTools.Field(NCreatureStateDisplayType, "_showHideTween") : null;
    private static readonly FieldInfo? StateDisplayOriginalPositionField =
        NCreatureStateDisplayType != null ? AccessTools.Field(NCreatureStateDisplayType, "_originalPosition") : null;
    private static readonly FieldInfo? StateDisplayHealthBarField =
        NCreatureStateDisplayType != null ? AccessTools.Field(NCreatureStateDisplayType, "_healthBar") : null;

    private static readonly Type? NHealthBarType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NHealthBar");
    private static readonly FieldInfo? HealthBarBlockTweenField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_blockTween") : null;
    private static readonly FieldInfo? HealthBarMiddlegroundTweenField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_middlegroundTween") : null;
    private static readonly FieldInfo? HealthBarBlockContainerField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_blockContainer") : null;
    private static readonly FieldInfo? HealthBarOriginalBlockPosField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_originalBlockPosition") : null;
    private static readonly FieldInfo? HealthBarCurrentHpRefreshField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_currentHpOnLastRefresh") : null;
    private static readonly FieldInfo? HealthBarMaxHpRefreshField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_maxHpOnLastRefresh") : null;
    private static readonly FieldInfo? HealthBarHpMiddlegroundField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_hpMiddleground") : null;
    private static readonly FieldInfo? HealthBarHpForegroundField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_hpForeground") : null;
    private static readonly MethodInfo? HealthBarRefreshValuesMethod =
        NHealthBarType != null ? AccessTools.Method(NHealthBarType, "RefreshValues") : null;

    // ── Potion visual refresh ──
    private static readonly Type? NPotionContainerType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotionContainer");
    private static readonly Type? NPotionHolderType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder");
    private static readonly Type? NPotionType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotion");
    private static readonly FieldInfo? ContainerHoldersField =
        NPotionContainerType != null ? AccessTools.Field(NPotionContainerType, "_holders") : null;
    private static readonly MethodInfo? HolderAddPotionMethod =
        NPotionHolderType != null ? AccessTools.Method(NPotionHolderType, "AddPotion") : null;
    private static readonly MethodInfo? NPotionCreateMethod =
        NPotionType != null ? AccessTools.Method(NPotionType, "Create",
            new[] { typeof(PotionModel) }) : null;
    private static readonly FieldInfo? HolderPotionBackingField =
        NPotionHolderType != null ? AccessTools.Field(NPotionHolderType, "<Potion>k__BackingField") : null;
    private static readonly FieldInfo? HolderDisabledField =
        NPotionHolderType != null ? AccessTools.Field(NPotionHolderType, "_disabledUntilPotionRemoved") : null;
    private static readonly FieldInfo? HolderEmptyIconField =
        NPotionHolderType != null ? AccessTools.Field(NPotionHolderType, "_emptyIcon") : null;

    // ── Pile count display ──
    private static readonly Type? NCombatCardPileType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCombatCardPile");
    private static readonly FieldInfo? PileButtonCountField =
        NCombatCardPileType != null ? AccessTools.Field(NCombatCardPileType, "_currentCount") : null;
    private static readonly FieldInfo? PileButtonLabelField =
        NCombatCardPileType != null ? AccessTools.Field(NCombatCardPileType, "_countLabel") : null;
    private static readonly FieldInfo? PileButtonPileField =
        NCombatCardPileType != null ? AccessTools.Field(NCombatCardPileType, "_pile") : null;

    // ═══════════════════════════════════════════════════════════
    //  PUBLIC ENTRY POINT
    // ═══════════════════════════════════════════════════════════

    public static void RefreshAllVisuals()
    {
        var cs = GetCombatState();
        if (cs == null) return;

        ResetEndTurnState();
        CleanupCardPlayVisuals();
        RefreshHandVisuals(cs);
        SnapHandPositions();

        // Refresh monster intents
        foreach (var creature in cs.Creatures)
        {
            if (creature.Monster == null) continue;
            var nCreature = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (nCreature != null)
                _ = nCreature.RefreshIntents();
        }

        // Refresh potion visuals for each player
        foreach (var ally in cs.Allies)
        {
            if (ally.Player != null)
                RefreshPotionVisuals(ally.Player);
        }

        RefreshPowerVisuals(cs);
        SyncPileCountDisplays();
        SnapCreatureVisuals(cs);

        ModEntry.Log("RefreshAllVisuals 완료, StateTracker 알림");
        var stateTracker = CombatManager.Instance?.StateTracker;
        if (stateTracker != null)
            NotifyCombatStateChangedMethod?.Invoke(stateTracker, new object[] { "UndoMod" });

        RefreshCardDescriptionsDeferred();
    }

    // ═══════════════════════════════════════════════════════════

    private static CombatState? GetCombatState()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return null;
        return CombatManagerStateField?.GetValue(cm) as CombatState;
    }

    private static void ResetEndTurnState()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return;

        // Clear _playersReadyToEndTurn
        try
        {
            var readySet = PlayersReadyToEndTurnField?.GetValue(cm);
            if (readySet is System.Collections.ICollection col && col.Count > 0)
            {
                readySet.GetType().GetMethod("Clear")?.Invoke(readySet, null);
                ModEntry.Log("ResetEndTurnState: _playersReadyToEndTurn 클리어");
            }
        }
        catch (Exception ex) { ModEntry.Log($"ResetEndTurnState error: {ex.Message}"); }

        // Clear _playersReadyToBeginEnemyTurn
        try
        {
            var readySet = PlayersReadyToBeginEnemyTurnField?.GetValue(cm);
            if (readySet is System.Collections.ICollection col && col.Count > 0)
                readySet.GetType().GetMethod("Clear")?.Invoke(readySet, null);
        }
        catch { }

        // PlayerActionsDisabled = false (PROPERTY setter → fires event!)
        try
        {
            if (PlayerActionsDisabledProp != null)
            {
                var current = (bool)(PlayerActionsDisabledProp.GetValue(cm) ?? false);
                if (current)
                {
                    PlayerActionsDisabledProp.SetValue(cm, false);
                    ModEntry.Log("ResetEndTurnState: PlayerActionsDisabled → false");
                }
            }
        }
        catch (Exception ex) { ModEntry.Log($"ResetEndTurnState PAD error: {ex.Message}"); }

        try { IsPlayPhaseProp?.SetValue(cm, true); } catch { }
        try { EndingPhaseOneProp?.SetValue(cm, false); } catch { }
        try { EndingPhaseTwoProp?.SetValue(cm, false); } catch { }
        try { IsEnemyTurnStartedProp?.SetValue(cm, false); } catch { }

        // NPlayerHand state reset
        var hand = NPlayerHand.Instance;
        if (hand == null) return;

        try
        {
            if (HandCurrentCardPlayField != null)
            {
                var currentPlay = HandCurrentCardPlayField.GetValue(hand);
                if (currentPlay != null)
                    HandCurrentCardPlayField.SetValue(hand, null);
            }

            if (HandCurrentModeField != null)
            {
                var mode = HandCurrentModeField.GetValue(hand);
                if (mode != null && (int)mode != (int)NPlayerHand.Mode.Play)
                    HandCurrentModeField.SetValue(hand, NPlayerHand.Mode.Play);
            }

            HandDraggedHolderIndexField?.SetValue(hand, -1);

            if (HandHoldersAwaitingQueueField?.GetValue(hand) is System.Collections.IDictionary awaitingQueue
                && awaitingQueue.Count > 0)
                awaitingQueue.Clear();

            if (HandIsDisabledField != null)
            {
                var isDisabled = (bool)(HandIsDisabledField.GetValue(hand) ?? false);
                if (isDisabled)
                {
                    HandIsDisabledField.SetValue(hand, false);
                    ((Control)hand).Modulate = Colors.White;
                }
            }
        }
        catch (Exception ex) { ModEntry.Log($"ResetEndTurnState hand error: {ex.Message}"); }
    }

    private static void CleanupCardPlayVisuals()
    {
        try
        {
            var playQueue = NCardPlayQueue.Instance;
            if (playQueue == null || PlayQueueField == null) return;

            var queueList = PlayQueueField.GetValue(playQueue) as System.Collections.IList;
            if (queueList == null || queueList.Count == 0) return;

            ModEntry.Log($"CleanupCardPlayVisuals: {queueList.Count}개 stale 엔트리 제거");

            foreach (var item in queueList)
            {
                if (item == null) continue;
                var itemType = item.GetType();

                var tweenField = AccessTools.Field(itemType, "currentTween");
                if (tweenField != null)
                {
                    var tween = tweenField.GetValue(item) as Tween;
                    if (tween != null && tween.IsValid())
                        tween.Kill();
                }

                var cardField = AccessTools.Field(itemType, "card");
                if (cardField != null)
                {
                    var nCard = cardField.GetValue(item) as NCard;
                    if (nCard != null && nCard.IsInsideTree())
                        nCard.QueueFree();
                }
            }

            queueList.Clear();
        }
        catch (Exception ex) { ModEntry.Log($"CleanupCardPlayVisuals error: {ex.Message}"); }
    }

    private static void RefreshHandVisuals(CombatState cs)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;

        // Find hand pile
        List<CardModel>? restoredHandCards = null;
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;
            foreach (var pile in player.PlayerCombatState.AllPiles)
            {
                if (pile.Type == PileType.Hand)
                {
                    restoredHandCards = pile.Cards.ToList();
                    break;
                }
            }
            if (restoredHandCards != null) break;
        }
        if (restoredHandCards == null) return;

        // Remove all current visual cards
        var currentVisualCards = new List<CardModel>();
        foreach (var holder in hand.ActiveHolders)
            currentVisualCards.Add(holder.CardNode.Model);

        foreach (var card in currentVisualCards)
            hand.Remove(card);

        // Re-create in correct order
        for (int i = 0; i < restoredHandCards.Count; i++)
        {
            var nCard = NCard.Create(restoredHandCards[i], ModelVisibility.Visible);
            nCard.Scale = Vector2.One;
            hand.Add(nCard, i);
        }

        hand.ForceRefreshCardIndices();
        ModEntry.Log($"RefreshHandVisuals: {currentVisualCards.Count} → {restoredHandCards.Count}장");
    }

    private static void SnapHandPositions()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || hand.ActiveHolders.Count == 0) return;

        InitHolderReflection();
        if (_holderType == null) return;

        foreach (var holder in hand.ActiveHolders)
        {
            var cancel = HolderPosCancelField?.GetValue(holder)
                as System.Threading.CancellationTokenSource;
            cancel?.Cancel();

            if (HolderTargetPosField != null)
                ((Control)holder).Position = (Vector2)HolderTargetPosField.GetValue(holder)!;

            if (HolderTargetAngleField != null && SetAngleInstantlyMethod != null)
                SetAngleInstantlyMethod.Invoke(holder,
                    new object[] { (float)HolderTargetAngleField.GetValue(holder)! });
            if (HolderTargetScaleField != null && SetScaleInstantlyMethod != null)
                SetScaleInstantlyMethod.Invoke(holder,
                    new object[] { (Vector2)HolderTargetScaleField.GetValue(holder)! });
        }
    }

    private static void InitHolderReflection()
    {
        if (_holderType != null) return;
        var hand = NPlayerHand.Instance;
        if (hand == null || hand.ActiveHolders.Count == 0) return;
        _holderType = hand.ActiveHolders[0].GetType();
        HolderTargetPosField = AccessTools.Field(_holderType, "_targetPosition");
        HolderPosCancelField = AccessTools.Field(_holderType, "_positionCancelToken");
        HolderTargetAngleField = AccessTools.Field(_holderType, "_targetAngle");
        HolderTargetScaleField = AccessTools.Field(_holderType, "_targetScale");
        SetAngleInstantlyMethod = AccessTools.Method(_holderType, "SetAngleInstantly");
        SetScaleInstantlyMethod = AccessTools.Method(_holderType, "SetScaleInstantly");
    }

    private static void RefreshPotionVisuals(Player player)
    {
        var nRun = NRun.Instance;
        if (nRun == null || NPotionContainerType == null) return;

        var container = FindNodeOfType(nRun, NPotionContainerType.Name);
        if (container == null) return;

        var holders = ContainerHoldersField?.GetValue(container) as System.Collections.IList;
        if (holders == null) return;

        for (int i = 0; i < holders.Count && i < player.PotionSlots.Count; i++)
        {
            var holder = (Node)holders[i]!;
            var desiredPotion = player.PotionSlots[i];

            try
            {
                foreach (var child in holder.GetChildren())
                {
                    if (NPotionType != null && NPotionType.IsInstanceOfType(child))
                    {
                        holder.RemoveChild(child);
                        ((Node)child).QueueFree();
                    }
                }
                HolderPotionBackingField?.SetValue(holder, null);
                HolderDisabledField?.SetValue(holder, false);
                ((Control)holder).Modulate = Colors.White;
                var emptyIcon = HolderEmptyIconField?.GetValue(holder) as Control;
                if (emptyIcon != null) emptyIcon.Modulate = Colors.White;

                if (desiredPotion != null)
                {
                    var nPotion = NPotionCreateMethod?.Invoke(null, new object[] { desiredPotion });
                    if (nPotion != null)
                    {
                        ((Node)nPotion).Set("position", new Vector2(-30f, -30f));
                        HolderAddPotionMethod?.Invoke(holder, new[] { nPotion });
                    }
                }
            }
            catch (Exception ex) { ModEntry.Log($"RefreshPotionVisuals: slot[{i}] ERROR: {ex.Message}"); }
        }
    }

    private static void RefreshPowerVisuals(CombatState cs)
    {
        if (NPowerContainerType == null || PowerContainerNodesField == null ||
            PowerContainerAddMethod == null) return;

        foreach (var creature in cs.Creatures)
        {
            try
            {
                var nCreature = NCombatRoom.Instance?.GetCreatureNode(creature);
                if (nCreature == null) continue;

                var stateDisplay = FindNodeOfType(nCreature,
                    NCreatureStateDisplayType?.Name ?? "NCreatureStateDisplay");
                if (stateDisplay == null) continue;

                var container = StateDisplayPowerContainerField?.GetValue(stateDisplay);
                if (container == null)
                    container = FindNodeOfType(nCreature, NPowerContainerType.Name);
                if (container == null) continue;

                var powerNodes = PowerContainerNodesField.GetValue(container) as System.Collections.IList;
                if (powerNodes != null)
                {
                    foreach (var node in powerNodes)
                    {
                        if (node is Node godotNode)
                            godotNode.QueueFree();
                    }
                    powerNodes.Clear();
                }

                foreach (var power in creature.Powers)
                    PowerContainerAddMethod.Invoke(container, new object[] { power });
            }
            catch (Exception ex) { ModEntry.Log($"RefreshPowerVisuals error: {ex.Message}"); }
        }
    }

    private static void SyncPileCountDisplays()
    {
        if (NCombatCardPileType == null || PileButtonCountField == null ||
            PileButtonLabelField == null || PileButtonPileField == null) return;

        var combatRoom = NCombatRoom.Instance;
        if (combatRoom == null) return;

        SyncPileCountsRecursive(combatRoom);
    }

    private static void SyncPileCountsRecursive(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            if (NCombatCardPileType!.IsInstanceOfType(child))
            {
                try
                {
                    var pile = PileButtonPileField!.GetValue(child) as CardPile;
                    if (pile != null)
                    {
                        int actualCount = pile.Cards.Count;
                        PileButtonCountField!.SetValue(child, actualCount);
                        var label = PileButtonLabelField!.GetValue(child);
                        if (label != null)
                        {
                            var setTextMethod = AccessTools.Method(label.GetType(), "SetTextAutoSize");
                            setTextMethod?.Invoke(label, new object[] { actualCount.ToString() });
                        }
                    }
                }
                catch { }
            }
            SyncPileCountsRecursive(child);
        }
    }

    private static void SnapCreatureVisuals(CombatState cs)
    {
        foreach (var creature in cs.Creatures)
        {
            var nCreature = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (nCreature == null) continue;

            // Intent fade tween
            try
            {
                var intentTween = NCreatureIntentFadeTweenField?.GetValue(nCreature) as Tween;
                if (intentTween != null && intentTween.IsValid()) intentTween.Kill();
                nCreature.IntentContainer.Modulate = Colors.White;
            }
            catch { }

            // Health bar
            try
            {
                var stateDisplay = FindNodeOfType(nCreature,
                    NCreatureStateDisplayType?.Name ?? "NCreatureStateDisplay");
                if (stateDisplay == null) continue;

                var showHideTween = StateDisplayShowHideTweenField?.GetValue(stateDisplay) as Tween;
                if (showHideTween != null && showHideTween.IsValid()) showHideTween.Kill();
                ((Control)stateDisplay).Modulate = Colors.White;
                if (StateDisplayOriginalPositionField?.GetValue(stateDisplay) is Vector2 origPos)
                    ((Control)stateDisplay).Position = origPos;

                var healthBar = StateDisplayHealthBarField?.GetValue(stateDisplay);
                if (healthBar == null) continue;

                var mgTween = HealthBarMiddlegroundTweenField?.GetValue(healthBar) as Tween;
                if (mgTween != null && mgTween.IsValid()) mgTween.Kill();

                var blockTween = HealthBarBlockTweenField?.GetValue(healthBar) as Tween;
                if (blockTween != null && blockTween.IsValid()) blockTween.Kill();

                var blockContainer = HealthBarBlockContainerField?.GetValue(healthBar) as Control;
                if (blockContainer != null && HealthBarOriginalBlockPosField?.GetValue(healthBar) is Vector2 blockPos)
                {
                    blockContainer.Position = blockPos;
                    blockContainer.Modulate = Colors.White;
                }

                HealthBarCurrentHpRefreshField?.SetValue(healthBar, -1);
                HealthBarMaxHpRefreshField?.SetValue(healthBar, -1);
                HealthBarRefreshValuesMethod?.Invoke(healthBar, null);

                var mgTween2 = HealthBarMiddlegroundTweenField?.GetValue(healthBar) as Tween;
                if (mgTween2 != null && mgTween2.IsValid()) mgTween2.Kill();

                var hpMiddleground = HealthBarHpMiddlegroundField?.GetValue(healthBar) as Control;
                var hpForeground = HealthBarHpForegroundField?.GetValue(healthBar) as Control;
                if (hpMiddleground != null && hpForeground != null)
                    hpMiddleground.OffsetRight = hpForeground.OffsetRight - 2f;
            }
            catch { }
        }
    }

    private static void RefreshCardDescriptionsDeferred()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;

        Callable.From(() =>
        {
            try
            {
                foreach (var holder in hand.ActiveHolders)
                    holder.CardNode?.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            }
            catch { }
        }).CallDeferred();

        _ = RefreshCardVisualsNextFrame(hand);
    }

    private static async Task RefreshCardVisualsNextFrame(NPlayerHand hand)
    {
        try
        {
            await hand.ToSignal(hand.GetTree(), SceneTree.SignalName.ProcessFrame);
            foreach (var holder in hand.ActiveHolders)
                holder.CardNode?.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
        }
        catch { }
    }

    private static Node? FindNodeOfType(Node parent, string typeName)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child.GetType().Name == typeName) return child;
            var found = FindNodeOfType(child, typeName);
            if (found != null) return found;
        }
        return null;
    }
}
