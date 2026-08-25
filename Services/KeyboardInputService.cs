using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimToAutoWirte.Services;

public sealed class KeyboardInputService
{
    // 额外标记用于低级键盘钩子识别本程序生成的替换输入，避免 SendInput 回到钩子后递归。
    internal const uint InjectionMarker = 0x4B4D4143;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualKeyTab = 0x09;

    public void SendCharacter(char character)
    {
        EnsureWindows();

        switch (character)
        {
            case '\r':
            case '\n':
                SendVirtualKey(VirtualKeyReturn);
                break;
            case '\t':
                SendVirtualKey(VirtualKeyTab);
                break;
            default:
                SendUnicode(character);
                break;
        }
    }

    /// <summary>一次性发送整段替换文本，不再按字符等待。</summary>
    public void SendText(string text, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 分批发送，避免超长宏超过 SendInput 的实用批量大小，且允许停止令牌及时生效。
        const int MaxInputsPerBatch = 1024;
        var inputs = new List<Input>(Math.Min(text.Length * 2, MaxInputsPerBatch));
        for (var index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = text[index];
            if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            if (character is '\r' or '\n')
            {
                AddVirtualKey(inputs, VirtualKeyReturn);
            }
            else if (character == '\t')
            {
                AddVirtualKey(inputs, VirtualKeyTab);
            }
            else
            {
                AddUnicode(inputs, character);
            }

            if (inputs.Count >= MaxInputsPerBatch)
            {
                Send(inputs.ToArray());
                inputs.Clear();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count > 0)
        {
            Send(inputs.ToArray());
        }
    }

    private static void SendUnicode(char character)
    {
        var inputs = new[]
        {
            CreateKeyboardInput(0, character, KeyEventUnicode),
            CreateKeyboardInput(0, character, KeyEventUnicode | KeyEventKeyUp),
        };

        Send(inputs);
    }

    private static void AddUnicode(ICollection<Input> inputs, char character)
    {
        inputs.Add(CreateKeyboardInput(0, character, KeyEventUnicode));
        inputs.Add(CreateKeyboardInput(0, character, KeyEventUnicode | KeyEventKeyUp));
    }

    private static void SendVirtualKey(ushort virtualKey)
    {
        var inputs = new[]
        {
            CreateKeyboardInput(virtualKey, '\0', 0),
            CreateKeyboardInput(virtualKey, '\0', KeyEventKeyUp),
        };

        Send(inputs);
    }

    private static void AddVirtualKey(ICollection<Input> inputs, ushort virtualKey)
    {
        inputs.Add(CreateKeyboardInput(virtualKey, '\0', 0));
        inputs.Add(CreateKeyboardInput(virtualKey, '\0', KeyEventKeyUp));
    }

    private static Input CreateKeyboardInput(ushort virtualKey, char scanCode, uint flags) =>
        new()
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = scanCode,
                    Flags = flags,
                    ExtraInfo = new UIntPtr(InjectionMarker),
                },
            },
        };

    private static void Send(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法发送键盘输入。");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("模拟外部窗口输入目前仅支持 Windows。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}
