using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimToAutoWirte.Models;

namespace SimToAutoWirte.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const uint ModifierNoRepeat = 0x4000;
    private const uint WindowMessageHotkey = 0x0312;
    private const uint WindowMessageQuit = 0x0012;

    private Thread? _messageThread;
    private uint _messageThreadId;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public Task<HotkeyRegistrationResult> RegisterAsync(IReadOnlyList<GlobalHotkeyBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new HotkeyRegistrationResult(false, "全局热键目前仅支持 Windows。"));
        }

        if (bindings.Count == 0)
        {
            return Task.FromResult(new HotkeyRegistrationResult(false, "没有可注册的热键。"));
        }

        var duplicate = FindDuplicate(bindings);
        if (duplicate is not null)
        {
            return Task.FromResult(new HotkeyRegistrationResult(
                false,
                $"热键 {duplicate.DisplayName} 被重复使用，请为每组宏选择不同的按键。"));
        }

        var completion = new TaskCompletionSource<HotkeyRegistrationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _messageThread = new Thread(() => RunMessageLoop(bindings, completion))
        {
            IsBackground = true,
            Name = "Global hotkey message loop",
        };
        _messageThread.Start();

        return completion.Task;
    }

    private void RunMessageLoop(
        IReadOnlyList<GlobalHotkeyBinding> bindings,
        TaskCompletionSource<HotkeyRegistrationResult> completion)
    {
        _messageThreadId = GetCurrentThreadId();
        var registeredIds = new List<int>(bindings.Count);

        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            if (RegisterHotKey(
                    IntPtr.Zero,
                    binding.Id,
                    binding.Hotkey.Modifiers | ModifierNoRepeat,
                    binding.Hotkey.VirtualKey))
            {
                registeredIds.Add(binding.Id);
                continue;
            }

            foreach (var registeredId in registeredIds)
            {
                UnregisterHotKey(IntPtr.Zero, registeredId);
            }

            completion.TrySetResult(new HotkeyRegistrationResult(
                false,
                $"热键 {binding.Hotkey.DisplayName} 注册失败，可能已被其他程序占用。"));
            _stopped.TrySetResult();
            return;
        }

        var callbacks = new Dictionary<int, Action>(bindings.Count);
        foreach (var binding in bindings)
        {
            callbacks[binding.Id] = binding.Callback;
        }

        completion.TrySetResult(new HotkeyRegistrationResult(true, string.Empty));

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Id == WindowMessageHotkey && callbacks.TryGetValue((int)message.WParam, out var callback))
                {
                    callback();
                }
            }
        }
        finally
        {
            foreach (var registeredId in registeredIds)
            {
                UnregisterHotKey(IntPtr.Zero, registeredId);
            }

            _stopped.TrySetResult();
        }
    }

    private static HotkeyOption? FindDuplicate(IReadOnlyList<GlobalHotkeyBinding> bindings)
    {
        var used = new HashSet<(uint VirtualKey, uint Modifiers)>();
        foreach (var binding in bindings)
        {
            var key = (binding.Hotkey.VirtualKey, binding.Hotkey.Modifiers);
            if (!used.Add(key))
            {
                return binding.Hotkey;
            }
        }

        return null;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _stopped.Task;
            return;
        }

        var thread = _messageThread;
        if (thread is { IsAlive: true } && _messageThreadId != 0)
        {
            PostThreadMessage(_messageThreadId, WindowMessageQuit, UIntPtr.Zero, IntPtr.Zero);
            try
            {
                await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // 不把消息线程退出延迟传递成 UI 异常；新服务会报告实际热键占用情况。
            }
        }
        else
        {
            _stopped.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_messageThread is { IsAlive: true } && _messageThreadId != 0)
        {
            PostThreadMessage(_messageThreadId, WindowMessageQuit, UIntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            _stopped.TrySetResult();
        }
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint minimumFilter, uint maximumFilter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

public sealed record GlobalHotkeyBinding(int Id, HotkeyOption Hotkey, Action Callback);

public sealed record HotkeyRegistrationResult(bool IsSuccessful, string ErrorMessage);
