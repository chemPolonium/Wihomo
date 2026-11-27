using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Wihomo.Services;

namespace Wihomo;

public partial class App : Application
{
    // 实例名、设置目录与端口都收敛到 AppInstance：默认实例与旧版互斥，
    // 设置 WIHOMO_INSTANCE_SUFFIX 时派生一个可并行运行的独立实例。
    private static string SingleInstanceMutexName => AppInstance.SingleInstanceMutexName;
    private static string ActivationPipeName => AppInstance.ActivationPipeName;

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _activationCancellation;
    private MainWindow? _mainWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                SignalExistingInstance();
            }
            finally
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            Exit();
            return;
        }

        _activationCancellation = new CancellationTokenSource();
        _ = Task.Run(() => ListenForActivationAsync(_activationCancellation.Token));

        _mainWindow = new MainWindow();
        _mainWindow.Closed += MainWindow_Closed;
        _mainWindow.Activate();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationCancellation = null;
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out);
            client.Connect(1000);
        }
        catch (TimeoutException ex)
        {
            ShowWarning($"无法连接到已运行的 Wihomo：{ex.Message}");
        }
        catch (IOException ex)
        {
            ShowWarning($"无法连接到已运行的 Wihomo：{ex.Message}");
        }
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                ActivationPipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _mainWindow?.DispatcherQueue.TryEnqueue(() => _mainWindow.RestoreAndActivate());
        }
    }

    private static void ShowWarning(string message)
    {
        var owner = IntPtr.Zero;
        MessageBoxW(owner, message, AppInstance.DisplayName, MbIconWarning);
    }

    private const uint MbIconWarning = 0x00000030;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
