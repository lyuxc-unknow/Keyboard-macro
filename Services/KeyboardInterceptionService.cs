using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SimToAutoWirte.Services;

/// <summary>
/// Windows 全局低级键盘钩子。启用后吞掉普通按键，并把替换请求交给异步消费者；
/// F1-F12 和修饰键始终放行，保证全局启停快捷键与系统快捷键仍可用。
/// </summary>
public sealed class KeyboardInterceptionService : IDisposable
{
    private const int HookKeyboardLowLevel = 13;
    private const uint MessageKeyDown = 0x0100;
    private const uint MessageSysKeyDown = 0x0104;
    private const uint InjectedEventFlag = 0x0010;
    private const int VirtualKeyF1 = 0x70;
    private const int VirtualKeyF12 = 0x7B;

    private readonly LowLevelKeyboardProc _hookCallback;
    private readonly ConcurrentQueue<ReplacementRequest> _replacementRequests = new();
    private readonly SemaphoreSlim _replacementSignal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _replacementWorker;
    private readonly TaskCompletionSource<HookRegistrationResult> _startCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<uint, byte> _interceptedKeys = new();

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private Func<Task>? _replacementRequested;
    private int _isEnabled;
    private long _generation;
    private int _hasStarted;
    private int _disposed;

    public KeyboardInterceptionService()
    {
        _hookCallback = HookCallback;
        _replacementWorker = Task.Run(ProcessReplacementRequestsAsync);
    }

    /// <summary>每个被拦截的普通按键触发一次替换文本输出。</summary>
    public Func<Task>? ReplacementRequested
    {
        get => Volatile.Read(ref _replacementRequested);
        set => Volatile.Write(ref _replacementRequested, value);
    }

    public bool IsEnabled => Volatile.Read(ref _isEnabled) != 0;

    public Task<HookRegistrationResult> StartAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new HookRegistrationResult(false, "键盘拦截目前仅支持 Windows。"));
        }

        if (Interlocked.Exchange(ref _hasStarted, 1) == 0)
        {
            _hookThread = new Thread(RunHookMessageLoop)
            {
                IsBackground = true,
                Name = "Low-level keyboard hook",
            };
            _hookThread.Start();
        }

        return _startCompletion.Task;
    }

    public void SetEnabled(bool enabled)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref _isEnabled, enabled ? 1 : 0);
        Interlocked.Increment(ref _generation);
        if (!enabled)
        {
            // 取消尚未开始的替换请求，并清除长按去重状态。
            while (_replacementRequests.TryDequeue(out _))
            {
            }
            _interceptedKeys.Clear();
            while (_replacementSignal.Wait(0))
            {
            }
        }
    }

    private void RunHookMessageLoop()
    {
        _hookThreadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(
            HookKeyboardLowLevel,
            _hookCallback,
            GetModuleHandle(null),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            _startCompletion.TrySetResult(new HookRegistrationResult(
                false,
                "键盘拦截启动失败，请确认应用拥有键盘监听权限。"));
            return;
        }

        _startCompletion.TrySetResult(new HookRegistrationResult(true, string.Empty));

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                // 低级键盘钩子由系统在消息循环线程上回调，不需要转发消息。
                _ = message;
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hookHandle, code, message, data);
        }

        var keyboardData = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
        if (IsInjected(keyboardData))
        {
            return CallNextHookEx(_hookHandle, code, message, data);
        }

        if (IsKeyUpMessage(message))
        {
            // 只吞掉曾经被我们吞掉的 key-up；停止拦截后普通 key-up 仍然正常传递。
            return _interceptedKeys.TryRemove(keyboardData.VirtualKey, out _)
                ? (IntPtr)1
                : CallNextHookEx(_hookHandle, code, message, data);
        }

        if (IsKeyDownMessage(message) && IsEnabled && ShouldIntercept(keyboardData.VirtualKey))
        {
            // 键盘长按会产生重复 WM_KEYDOWN。一个物理按键只触发一次替换。
            if (_interceptedKeys.TryAdd(keyboardData.VirtualKey, 0))
            {
                _replacementRequests.Enqueue(new ReplacementRequest(Volatile.Read(ref _generation)));
                _replacementSignal.Release();
            }

            // 只要处于拦截状态就必须吞掉这次按键，与是否触发替换无关。
            // 旧实现在 TryAdd 失败时会落到下面的 CallNextHookEx，把用户真实的按键放行到
            // 目标程序——长按自动重复、或上一次 key-up 丢失导致按键滞留在字典里时都会发生，
            // 于是真实字符混进宏输出，看起来就是"莫名其妙多出字符、代码错乱"。
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookHandle, code, message, data);
    }

    private async Task ProcessReplacementRequestsAsync()
    {
        try
        {
            while (true)
            {
                await _replacementSignal.WaitAsync(_lifetime.Token);
                if (!_replacementRequests.TryDequeue(out var request))
                {
                    continue;
                }

                if (!IsEnabled || request.Generation != Volatile.Read(ref _generation))
                {
                    continue;
                }

                var replacement = ReplacementRequested;
                if (replacement is null)
                {
                    continue;
                }

                try
                {
                    await replacement();
                }
                catch (OperationCanceledException)
                {
                    // 停止拦截时取消当前替换，不让后台 worker 退出。
                }
                catch (Exception exception)
                {
                    ReplacementFailed?.Invoke(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose 通过取消生命周期结束 worker。
        }
    }

    private static bool IsKeyDownMessage(IntPtr message) =>
        (uint)message == MessageKeyDown || (uint)message == MessageSysKeyDown;

    private static bool IsKeyUpMessage(IntPtr message) =>
        (uint)message == 0x0101 || (uint)message == 0x0105;

    private static bool IsInjected(LowLevelKeyboardInput input) =>
        (input.Flags & InjectedEventFlag) != 0
        || input.ExtraInfo == new UIntPtr(KeyboardInputService.InjectionMarker);

    private static bool ShouldIntercept(uint virtualKey)
    {
        // F 区永远放行，避免拦截启动/停止宏所依赖的功能键。
        if (virtualKey is >= VirtualKeyF1 and <= VirtualKeyF12)
        {
            return false;
        }

        // 修饰键也放行，避免 Ctrl/Alt/Shift 出现按下后无法释放的状态。
        return virtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C);
    }

    public void Dispose()
    {
        Volatile.Write(ref _isEnabled, 0);
        _interceptedKeys.Clear();
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        var thread = _hookThread;
        if (thread is { IsAlive: true } && _hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, MessageQuit, UIntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(1));
        }

        try
        {
            _replacementWorker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _replacementSignal.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>替换输出失败时由宿主更新状态，不会让钩子 worker 退出。</summary>
    public Action<Exception>? ReplacementFailed { get; set; }

    private const uint MessageQuit = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point CursorPosition;
        public uint Private;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    private readonly record struct ReplacementRequest(long Generation);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint minimumFilter, uint maximumFilter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

public sealed record HookRegistrationResult(bool IsSuccessful, string ErrorMessage);
