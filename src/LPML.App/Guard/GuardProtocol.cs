using System.IO;
using LPML.Core;

namespace LPML.App.Guard;

/// <summary>
/// 主进程退出原因。主进程正常退出时写进最后一条日志并通过管道告诉守护进程；
/// 守护进程在没收到通知时自行判定是「外部结束 / 崩溃 / 看门狗强杀」。
/// </summary>
public enum GuardExitReason
{
    Unknown = 0,
    UserClosedWindow,
    ApplicationExit,
    CliFinished,
    CliInterrupted,
    StartupFailed,
    ExternalTermination,
    KilledByGuard,
    NeverConnected
}

internal static class GuardExitReasonText
{
    public static string Describe(GuardExitReason reason, bool isZh) => reason switch
    {
        GuardExitReason.UserClosedWindow => isZh ? "用户关闭了启动器窗口" : "User closed the launcher window",
        GuardExitReason.ApplicationExit => isZh ? "启动器正常退出" : "Launcher exited normally",
        GuardExitReason.CliFinished => isZh ? "命令行任务结束" : "CLI task finished",
        GuardExitReason.CliInterrupted => isZh ? "命令行被中断（Ctrl+C）" : "CLI interrupted (Ctrl+C)",
        GuardExitReason.StartupFailed => isZh ? "启动失败，已强制结束进程" : "Startup failed; process terminated",
        GuardExitReason.ExternalTermination => isZh ? "被外部结束或崩溃（未收到正常退出通知）" : "Terminated externally or crashed (no shutdown notice)",
        GuardExitReason.KilledByGuard => isZh ? "由守护进程强制关闭（界面未响应）" : "Force-closed by guard (UI unresponsive)",
        GuardExitReason.NeverConnected => isZh ? "主进程未完成连接（启动早期就退出了）" : "Host never connected (exited during startup)",
        _ => isZh ? "原因未知" : "Unknown reason"
    };
}

/// <summary>
/// 主进程 ↔ 守护进程的两条命名管道：
///   log  —— 主进程把日志帧 / 退出帧发过来，守护进程统一写文件
///   hb   —— 主进程 UI 线程的心跳，守护进程据此判断"未响应"
/// 会话号用 GUID，保证多开时互不串台。
/// </summary>
internal static class GuardProtocol
{
    /// <summary>守护进程 → 主进程：可以接管日志了。</summary>
    public const byte Ready = 0x01;

    /// <summary>主进程 → 守护进程：一条日志。</summary>
    public const byte FrameEntry = 0x10;

    /// <summary>主进程 → 守护进程：正常退出，后跟一个 int 原因。</summary>
    public const byte FrameShutdown = 0x11;

    public static string LogPipe(string session) => "lpml-log-" + session;
    public static string HeartbeatPipe(string session) => "lpml-hb-" + session;

    /// <summary>
    /// 用 BinaryWriter 写字段而不是按行读：日志正文里常带换行（异常堆栈），
    /// Write(string) 自带长度前缀，能完整还原。帧首字节标明类型。
    /// </summary>
    public static void WriteEntry(BinaryWriter w, LogEntry e)
    {
        w.Write(FrameEntry);
        w.Write(e.Time.Ticks);
        w.Write((int)e.Level);
        w.Write(e.Source ?? "");
        w.Write(e.Message ?? "");
        w.Write(e.Seq);
        w.Flush();
    }

    /// <summary>正常退出帧。必须是主进程写往日志管道的最后一段数据。</summary>
    public static void WriteShutdown(BinaryWriter w, GuardExitReason reason)
    {
        w.Write(FrameShutdown);
        w.Write((int)reason);
        w.Flush();
    }

    /// <summary>
    /// 读一帧。日志帧填 entry，退出帧填 reason；未知帧类型原样返回，由调用方跳过。
    /// 读到流末尾会抛 EndOfStreamException / IOException，调用方视为连接关闭。
    /// </summary>
    public static byte ReadFrame(BinaryReader r, out LogEntry? entry, out GuardExitReason reason)
    {
        var kind = r.ReadByte();
        entry = null;
        reason = GuardExitReason.Unknown;

        if (kind == FrameEntry)
        {
            var ticks = r.ReadInt64();
            var level = (LogLevel)r.ReadInt32();
            var source = r.ReadString();
            var message = r.ReadString();
            var seq = r.ReadInt64();
            entry = new LogEntry(new DateTime(ticks), level, source, message, seq);
        }
        else if (kind == FrameShutdown)
        {
            reason = (GuardExitReason)r.ReadInt32();
        }
        return kind;
    }

    public static void WriteHeartbeat(BinaryWriter w)
    {
        w.Write(Environment.TickCount64);
        w.Flush();
    }

    public static long ReadHeartbeat(BinaryReader r) => r.ReadInt64();
}
