using System.Diagnostics;
using System.IO;
using System.Text;

namespace Wihomo.Services;

public sealed class MihomoProcessManager
{
    private Process? _process;

    public event Action<string>? OutputReceived;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string executablePath, string workingDirectory, string configFilePath)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("mihomo core is already running.");
        }

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("mihomo core executable not found.", executablePath);
        }

        Directory.CreateDirectory(workingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            Arguments = $"-d \"{workingDirectory}\" -f \"{configFilePath}\""
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start mihomo core process.");
        _process.OutputDataReceived += Process_OutputDataReceived;
        _process.ErrorDataReceived += Process_OutputDataReceived;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }

        _process.Dispose();
        _process = null;
    }

    private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            OutputReceived?.Invoke(e.Data);
        }
    }
}
