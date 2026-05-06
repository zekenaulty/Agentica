namespace Agentica.CLI.Scenarios.MazeQuest;

public sealed class MazeQuestWatchControl : IDisposable
{
    private readonly CancellationTokenSource _runCancellation;
    private readonly object _gate = new();
    private Thread? _inputThread;
    private bool _disposed;
    private bool _paused;

    public MazeQuestWatchControl(CancellationTokenSource runCancellation)
    {
        _runCancellation = runCancellation;
    }

    public bool InputEnabled { get; private set; }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    public void Start()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        InputEnabled = true;
        _inputThread = new Thread(ReadInputLoop)
        {
            IsBackground = true,
            Name = "MazeQuest watch controls"
        };
        _inputThread.Start();
    }

    public void WaitIfPaused(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_runCancellation.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            lock (_gate)
            {
                if (!_paused)
                {
                    return;
                }

                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(100));
            }
        }
    }

    public void PrintControls()
    {
        if (!InputEnabled)
        {
            Console.WriteLine("Watch controls: input is redirected; use Ctrl+C or --timeout-seconds to stop.");
            return;
        }

        Console.WriteLine("Watch controls: p pause, r resume, s stop, q quit, Ctrl+C cancel.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _paused = false;
            Monitor.PulseAll(_gate);
        }
    }

    private void ReadInputLoop()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_disposed || _runCancellation.IsCancellationRequested)
                {
                    return;
                }
            }

            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            switch (char.ToLowerInvariant(key.KeyChar))
            {
                case 'p':
                    SetPaused(true);
                    break;
                case 'r':
                    SetPaused(false);
                    break;
                case 's':
                case 'q':
                    Console.WriteLine();
                    Console.WriteLine("[watch] Stop requested.");
                    _runCancellation.Cancel();
                    SetPaused(false);
                    return;
            }
        }
    }

    private void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_disposed || _paused == paused)
            {
                return;
            }

            _paused = paused;
            Console.WriteLine();
            Console.WriteLine(paused ? "[watch] Paused. Press r to resume or s to stop." : "[watch] Resumed.");
            Monitor.PulseAll(_gate);
        }
    }
}
