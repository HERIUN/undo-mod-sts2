using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace UndoMod;

[ModInitializer(nameof(Initialize))]
public class ModEntry
{
    public static void Initialize()
    {
        var harmony = new Harmony("undo_mod.patch");
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        try { UndoInput.Start(); }
        catch (Exception ex) { Log($"UndoInput 시작 실패 (무시): {ex.Message}"); }

        try { UndoTcpServer.Start(); }
        catch (Exception ex) { Log($"TCP 서버 시작 실패 (무시): {ex.Message}"); }

        Log("UndoMod 로드됨 (Ctrl+Z = Undo, TCP 포트 " + UndoTcpServer.Port + ")");
    }

    internal static void Log(string msg)
    {
        Godot.GD.Print("[UndoMod] " + msg);
    }
}
