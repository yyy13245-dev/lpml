using LPML.Core;
using LPML.Core.Multiplayer;
using System;
using System.Threading.Tasks;

namespace LPML.App.Services;

/// <summary>
/// 「尝试重新启动联机核心」按钮的共用逻辑：三个联机页面（入口 / 创建 / 加入）
/// 共用同一套判定与重启流程，避免各写一份导致行为不一致。
/// </summary>
public static class MultiplayerCoreRetry
{
    /// <summary>
    /// 核心不可用：已安装但上报了异常，或既没有存活进程也没有可用 API 端口。
    /// 未安装不属于「启动失败」，这时应该去下载核心，不给重试按钮。
    /// installed 由调用方传入（状态轮询每 500ms 一次，避免重复做校验哈希）。
    /// </summary>
    public static bool NeedsRestart(TerracottaCore core, bool installed)
        => installed && (core.StateKind == TerracottaStateKind.Exception
                         || (!core.IsRunning && core.ApiPort <= 0));

    /// <summary>
    /// 后台重启核心。onBusy 控制按钮禁用态，成功后 onOk，失败后 onFail 收到已本地化的消息。
    /// </summary>
    public static async Task RunAsync(
        TerracottaCore core,
        Action<bool> onBusy,
        Action onOk,
        Action<string> onFail)
    {
        onBusy(true);
        try
        {
            await core.RestartAsync().ConfigureAwait(true);
            Log.Info("Multi", "联机核心重新启动成功");
            onOk();
        }
        catch (OperationCanceledException)
        {
            onFail(Lang.T("String.Multi.Err.Canceled"));
        }
        catch (Exception ex)
        {
            Log.Warn("Multi", $"重新启动联机核心失败：{ex.GetType().Name}: {ex.Message}");
            onFail(Lang.F("String.Multi.Err.RestartFailed", ex.Message));
        }
        finally
        {
            onBusy(false);
        }
    }
}
