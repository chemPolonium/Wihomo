using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace Wihomo;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\Wihomo.SingleInstance";
    private const string ActivationPipeName = "Wihomo.Activation";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _activationCancellation;

    protected override void OnStartup(StartupEventArgs e)
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

            Shutdown();
            return;
        }

        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _activationCancellation = new CancellationTokenSource();
        _ = Task.Run(() => ListenForActivationAsync(_activationCancellation.Token));
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
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
            System.Windows.MessageBox.Show(
                $"无法连接到已运行的 Wihomo：{ex.Message}",
                "Wihomo",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (IOException ex)
        {
            System.Windows.MessageBox.Show(
                $"无法连接到已运行的 Wihomo：{ex.Message}",
                "Wihomo",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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

            await Dispatcher.InvokeAsync(ActivateMainWindow);
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.RestoreAndActivate();
        }
    }
}
