using System;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Combat;

namespace UndoMod;

/// <summary>
/// Ctrl+Z로 Undo 실행.
/// Godot의 C# 가상 메서드(_Ready/_Process)는 모드 DLL에서 바인딩되지 않으므로,
/// 별도 스레드에서 키보드 폴링 후 메인 스레드에서 실행.
/// </summary>
public static class UndoInput
{
    private static Thread? _thread;
    private static volatile bool _running;
    private static volatile bool _undoRequested;

    public static void Start()
    {
        if (_thread != null) return;
        _running = true;

        // Godot SceneTree의 process_frame 시그널에 콜백 연결
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
        {
            tree.ProcessFrame += OnProcessFrame;
            ModEntry.Log("ProcessFrame 시그널 연결됨");
        }

        // 키보드 폴링 스레드
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "UndoInputPoll" };
        _thread.Start();
        ModEntry.Log("Undo 입력 감지 시작 (Ctrl+Z)");
    }

    private static void PollLoop()
    {
        bool wasPressed = false;
        while (_running)
        {
            try
            {
                // Godot Input은 메인 스레드에서만 안전하지만,
                // 읽기는 대부분 스레드 세이프
                bool pressed = Input.IsKeyPressed(Key.Z) && Input.IsKeyPressed(Key.Ctrl);
                if (pressed && !wasPressed)
                {
                    _undoRequested = true;
                }
                wasPressed = pressed;
            }
            catch { }
            Thread.Sleep(50); // 20Hz 폴링
        }
    }

    private static void OnProcessFrame()
    {
        if (_undoRequested)
        {
            _undoRequested = false;
            UndoManager.Undo();
        }
    }

    public static void Stop()
    {
        _running = false;
    }
}
