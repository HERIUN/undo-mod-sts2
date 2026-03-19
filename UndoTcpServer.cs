using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Godot;

namespace UndoMod;

/// <summary>
/// Undo 명령을 받는 TCP 서버 (포트 38643).
///
/// 프로토콜:
///   클라이언트 → "undo\n"  → 서버: Undo 실행 후 "ok\n" 또는 "fail\n" 응답
///   클라이언트 → "save\n"  → 서버: 스냅샷 저장 후 "ok\n" 응답
///   클라이언트 → "count\n" → 서버: 스냅샷 개수 응답 (예: "3\n")
///   클라이언트 → "clear\n" → 서버: 스냅샷 초기화 후 "ok\n" 응답
///
/// Undo/Save는 메인 스레드에서 실행해야 하므로 플래그 기반으로 처리.
/// </summary>
public static class UndoTcpServer
{
    public const int Port = 38643;
    private static TcpListener? _listener;
    private static Thread? _thread;

    // 메인 스레드 실행용 플래그
    private static volatile string? _pendingCommand;
    private static volatile string? _pendingResult;
    private static readonly object _lock = new();

    public static void Start()
    {
        if (_listener != null) return;
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _thread = new Thread(ListenLoop) { IsBackground = true, Name = "UndoTcpServer" };
        _thread.Start();

        // ProcessFrame에서 명령 처리
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
        {
            tree.ProcessFrame += OnProcessFrame;
        }

        ModEntry.Log($"Undo TCP 서버 시작 (포트 {Port})");
    }

    private static void OnProcessFrame()
    {
        if (_pendingCommand == null) return;

        string cmd;
        lock (_lock)
        {
            cmd = _pendingCommand;
            if (cmd == null) return;
        }

        string result;
        try
        {
            switch (cmd)
            {
                case "undo":
                    result = UndoManager.Undo() ? "ok" : "fail";
                    break;
                case "save":
                    UndoManager.SaveSnapshot();
                    result = "ok";
                    break;
                case "clear":
                    UndoManager.Clear();
                    result = "ok";
                    break;
                case "count":
                    result = UndoManager.Count.ToString();
                    break;
                default:
                    result = "unknown";
                    break;
            }
        }
        catch (Exception ex)
        {
            result = $"error:{ex.Message}";
            ModEntry.Log($"명령 실행 오류 ({cmd}): {ex.Message}");
        }

        lock (_lock)
        {
            _pendingResult = result;
            _pendingCommand = null;
        }
    }

    private static void ListenLoop()
    {
        while (true)
        {
            try
            {
                using var client = _listener!.AcceptTcpClient();
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                var stream = client.GetStream();
                var buf = new byte[64];
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) continue;

                string cmd = Encoding.UTF8.GetString(buf, 0, n).Trim().ToLower();

                // count는 스레드 세이프하므로 직접 응답
                if (cmd == "count")
                {
                    string resp = UndoManager.Count.ToString() + "\n";
                    stream.Write(Encoding.UTF8.GetBytes(resp));
                    continue;
                }

                // 메인 스레드에서 실행할 명령 등록
                lock (_lock)
                {
                    _pendingCommand = cmd;
                    _pendingResult = null;
                }

                // 결과 대기 (최대 3초)
                string? result = null;
                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(50);
                    lock (_lock)
                    {
                        if (_pendingResult != null)
                        {
                            result = _pendingResult;
                            _pendingResult = null;
                            break;
                        }
                    }
                }

                result ??= "timeout";
                stream.Write(Encoding.UTF8.GetBytes(result + "\n"));
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Undo TCP 오류: {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }
}
