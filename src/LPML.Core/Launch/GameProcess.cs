using System.Diagnostics;

namespace LPML.Core.Launch;

/// <summary>启动并监控 Minecraft 进程，实时输出游戏日志。</summary>
public sealed class GameProcess
{
    private Process? _process;

    public int? ProcessId => _process?.Id;
    public bool IsRunning => _process is not null && !_process.HasExited;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<int>? Exited;

    public ProcessStartInfo BuildStartInfo(string javaPath, IEnumerable<string> jvmArguments,
        string mainClass, IEnumerable<string> gameArguments, string workingDirectory,
        IDictionary<string, string>? environment = null)
    {
        var args = new List<string>();
        args.AddRange(jvmArguments);
        args.Add(mainClass);
        args.AddRange(gameArguments);

        var startInfo = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);

        if (environment is not null)
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;

        return startInfo;
    }

    public int Start(ProcessStartInfo startInfo)
    {
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            GameOutputHub.Push(e.Data);
            OutputReceived?.Invoke(this, e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            GameOutputHub.Push(e.Data);
            OutputReceived?.Invoke(this, e.Data);
        };
        _process.Exited += (_, _) => Exited?.Invoke(this, _process.ExitCode);

        // 进程优先级
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return _process.Id;
    }

    /// <summary>向游戏进程的标准输入写一行（服务端可用，客户端一般忽略）。</summary>
    public bool SendInput(string line)
    {
        if (_process is null) return false;
        try
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
            return true;
        }
        catch (ObjectDisposedException) { return false; }
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>标准输入是否仍可用。</summary>
    public bool CanSendInput
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    public void SetPriority(string priority)
    {
        if (_process is null) return;
        try
        {
            _process.PriorityClass = priority?.ToLowerInvariant() switch
            {
                "idle" => ProcessPriorityClass.Idle,
                "belownormal" => ProcessPriorityClass.BelowNormal,
                "normal" => ProcessPriorityClass.Normal,
                "abovenormal" => ProcessPriorityClass.AboveNormal,
                "high" => ProcessPriorityClass.High,
                "realtime" => ProcessPriorityClass.RealTime,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async Task<int> WaitForExitAsync(CancellationToken token = default)
    {
        if (_process is null) return -1;
        await _process.WaitForExitAsync(token).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
