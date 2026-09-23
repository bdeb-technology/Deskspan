using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Deskspan.Net;

namespace Deskspan.Input;

public sealed class InputHub : IDisposable
{
    private const int KeyboardHookId = 13;
    private const int MouseHookId = 14;
    private const uint WmInput = 0x00FF;
    private const uint WmHotkey = 0x0312;
    private const uint WmRefreshHotkey = 0x8001;
    private const uint WmQuit = 0x0012;
    private const int HotkeyId = 1;
    private const uint RidInput = 0x10000003;
    private const uint InputSink = 0x00000100;
    private static readonly nint MessageOnly = new(-3);

    private static InputHub? _current;
    private static readonly NativeMethods.HookProc KeyboardProcedure = OnKeyboard;
    private static readonly NativeMethods.HookProc MouseProcedure = OnMouse;

    private readonly NativeMethods.WindowProc _windowProcedure;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly byte[] _rawBuffer = new byte[1024];
    private readonly GCHandle _rawPin;
    private Thread? _thread;
    private uint _threadId;
    private nint _keyboardHook;
    private nint _mouseHook;
    private readonly ModifierState _modifiers = new();
    private int _captureMouse;
    private int _captureKeyboard;
    private int _hotkeyEnabled = 1;
    private int _systemHotkey;
    private long _lastToggle;
    private object _hotkey = Hotkey.Default;
    private int _speed = 10;
    private nint _hwnd;
    private bool _triggerDown;
    private int _suppressedMouseButton;
    private readonly ConcurrentQueue<HookKey> _pendingKeys = new();
    private int _drainingKeys;
    private long _duplicateTick;
    private int _duplicateId;

    public InputHub()
    {
        _windowProcedure = OnWindow;
        _rawPin = GCHandle.Alloc(_rawBuffer, GCHandleType.Pinned);
    }

    public event Action? HotkeyPressed;
    public event Action<int, int>? MouseDelta;
    public event Action<byte, bool>? PointerButton;
    public event Action<int, bool>? WheelMoved;
    public event Action<ushort, ushort, bool, bool>? KeyReceived;

    public bool CaptureMouse
    {
        get => Volatile.Read(ref _captureMouse) == 1;
        set
        {
            Volatile.Write(ref _captureMouse, value ? 1 : 0);
            if (value)
            {
                _speed = InputInjector.PointerSpeedScale();
                InputInjector.ClipToCurrentCursor();
            }
            else
            {
                InputInjector.ReleaseClip();
            }
        }
    }

    public bool CaptureKeyboard
    {
        get => Volatile.Read(ref _captureKeyboard) == 1;
        set => Volatile.Write(ref _captureKeyboard, value ? 1 : 0);
    }

    public bool HotkeyEnabled
    {
        get => Volatile.Read(ref _hotkeyEnabled) == 1;
        set
        {
            Volatile.Write(ref _hotkeyEnabled, value ? 1 : 0);
            RequestHotkeyRefresh();
        }
    }

    public bool KeyboardHookInstalled => _keyboardHook != 0;

    public bool SystemHotkeyInstalled => Volatile.Read(ref _systemHotkey) == 1;

    public Hotkey Hotkey
    {
        get => CurrentHotkey;
        set
        {
            _hotkey = value;
            RequestHotkeyRefresh();
        }
    }

    private Hotkey CurrentHotkey => (Hotkey)_hotkey;

    public void Start()
    {
        if (_thread != null)
            return;
        _current = this;
        _thread = new Thread(Run) { IsBackground = false, Name = "Deskspan input", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        CaptureMouse = false;
        CaptureKeyboard = false;
        if (ReferenceEquals(_current, this))
            _current = null;
        if (_threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, WmQuit, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(2));
        if (_rawPin.IsAllocated)
            _rawPin.Free();
        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            var className = "DeskspanInputSink";
            var instance = NativeMethods.GetModuleHandle(null);
            var windowClass = new NativeMethods.WNDCLASS
            {
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                Instance = instance,
                ClassName = className
            };
            NativeMethods.RegisterClass(ref windowClass);
            var hwnd = NativeMethods.CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, MessageOnly, 0, instance, 0);
            var devices = new[]
            {
                new NativeMethods.RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = InputSink, Target = hwnd },
                new NativeMethods.RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = InputSink, Target = hwnd }
            };
            NativeMethods.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());
            _hwnd = hwnd;
            InstallHooks(instance);
            RefreshRegisteredHotkey(hwnd);
            NativeMethods.PeekMessage(out _, 0, 0, 0, 0);
            _threadId = NativeMethods.GetCurrentThreadId();
            _ready.Set();
            while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == WmRefreshHotkey)
                {
                    RefreshRegisteredHotkey(_hwnd);
                    continue;
                }

                if (message.Message == WmHotkey)
                {
                    if (HotkeyEnabled)
                        QueueToggle();
                    continue;
                }

                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }

            NativeMethods.UnregisterHotKey(hwnd, HotkeyId);
            if (_keyboardHook != 0)
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0)
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
            if (hwnd != 0)
                NativeMethods.DestroyWindow(hwnd);
        }
        catch (Exception)
        {
            _ready.Set();
        }
    }

    private nint OnWindow(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmInput && (CaptureMouse || CaptureKeyboard))
            ReadRaw(lParam);
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ReadRaw(nint handle)
    {
        var headerSize = (uint)RawInputParser.HeaderSize;
        var size = (uint)_rawBuffer.Length;
        var read = NativeMethods.GetRawInputData(handle, RidInput, _rawPin.AddrOfPinnedObject(), ref size, headerSize);
        if (read == uint.MaxValue || read == 0 || read > _rawBuffer.Length)
            return;
        var span = _rawBuffer.AsSpan(0, (int)read);
        if (RawInputParser.TryParseMouse(span, out var pointer))
        {
            if (!CaptureMouse || pointer.ExtraInformation == (uint)NativeMethods.Stamp)
                return;
            if (pointer.HasMove)
            {
                var dx = (int)Math.Round(pointer.DeltaX * (_speed / 10.0));
                var dy = (int)Math.Round(pointer.DeltaY * (_speed / 10.0));
                if (dx != 0 || dy != 0)
                    MouseDelta?.Invoke(dx, dy);
            }

            EmitButtons(pointer.ButtonFlags, pointer.ButtonData);
            return;
        }

        if (!CaptureKeyboard || !RawInputParser.TryParseKey(span, out var key) || key.ExtraInformation == (uint)NativeMethods.Stamp)
            return;
        var hotkey = CurrentHotkey;
        if (key.VirtualKey == hotkey.VirtualKey && hotkey.ModifiersMatch(vk => _modifiers.TrackedDown(vk, false), IsKeyDown))
            return;
        PostKey(new HookKey(key.VirtualKey, key.ScanCode, !key.IsUp, key.Extended));
    }

    private void PostKey(HookKey key)
    {
        var id = (key.VirtualKey << 1) | (key.Down ? 1 : 0);
        var now = Environment.TickCount64;
        if (id == _duplicateId && now - _duplicateTick < 8)
            return;
        _duplicateId = id;
        _duplicateTick = now;
        _pendingKeys.Enqueue(key);
        if (Interlocked.CompareExchange(ref _drainingKeys, 1, 0) != 0)
            return;
        ThreadPool.QueueUserWorkItem(_ => DrainKeys());
    }

    private void DrainKeys()
    {
        try
        {
            while (_pendingKeys.TryDequeue(out var key))
                KeyReceived?.Invoke(key.VirtualKey, key.ScanCode, key.Down, key.Extended);
        }
        finally
        {
            Interlocked.Exchange(ref _drainingKeys, 0);
            if (!_pendingKeys.IsEmpty && Interlocked.CompareExchange(ref _drainingKeys, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ => DrainKeys());
        }
    }

    private void EmitButtons(ushort flags, ushort data)
    {
        void Raise(ushort downMask, ushort upMask, byte button)
        {
            if (Suppressed(button))
            {
                if ((flags & upMask) != 0)
                    _suppressedMouseButton = 0;
                return;
            }

            if ((flags & downMask) != 0)
                PointerButton?.Invoke(button, true);
            if ((flags & upMask) != 0)
                PointerButton?.Invoke(button, false);
        }

        Raise(0x0001, 0x0002, 0);
        Raise(0x0004, 0x0008, 1);
        Raise(0x0010, 0x0020, 2);
        Raise(0x0040, 0x0080, 3);
        Raise(0x0100, 0x0200, 4);
        if ((flags & 0x0400) != 0)
            WheelMoved?.Invoke((short)data, false);
        if ((flags & 0x0800) != 0)
            WheelMoved?.Invoke((short)data, true);
    }

    private static nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        var self = _current;
        if (code < 0 || self == null)
            return NativeMethods.CallNextHookEx(self?._keyboardHook ?? 0, code, wParam, lParam);
        var data = Marshal.PtrToStructure<NativeMethods.KeyboardHook>(lParam);
        var injected = data.ExtraInfo == NativeMethods.Stamp;
        var down = wParam == 0x0100 || wParam == 0x0104;
        var up = wParam == 0x0101 || wParam == 0x0105;
        var hotkey = self.CurrentHotkey;
        if (!injected && (down || up))
            self._modifiers.Note((int)data.VirtualKey, down);
        var trigger = (int)data.VirtualKey == hotkey.VirtualKey;
        if (!injected && trigger && down)
        {
            if (self._triggerDown)
                down = false;
            self._triggerDown = true;
        }
        else if (!injected && trigger && up)
        {
            self._triggerDown = false;
        }

        var altDown = (data.Flags & 0x20) != 0;
        var matched = self.HotkeyEnabled && hotkey.TriggerDown((int)data.VirtualKey, down, vk => self._modifiers.TrackedDown(vk, altDown), IsKeyDown);
        var action = HookPolicy.Decide(self.CaptureKeyboard, injected, matched);
        if (action == HookAction.SwallowAndToggle)
        {
            self.QueueToggle();
            return 1;
        }

        if (action == HookAction.Swallow)
        {
            if (HookKey.TryCreate(data.VirtualKey, data.ScanCode, data.Flags, down, up, out var captured))
                self.PostKey(captured);
            return 1;
        }
        return NativeMethods.CallNextHookEx(self._keyboardHook, code, wParam, lParam);
    }

    private static nint OnMouse(int code, nint wParam, nint lParam)
    {
        var self = _current;
        if (code < 0 || self == null)
            return NativeMethods.CallNextHookEx(self?._mouseHook ?? 0, code, wParam, lParam);
        var data = Marshal.PtrToStructure<NativeMethods.MouseHook>(lParam);
        var injected = data.ExtraInfo == NativeMethods.Stamp;
        var button = MouseChord.Button((int)wParam, data.MouseData, out var down);
        if (!injected && button != 0 && self.HotkeyEnabled && self.CurrentHotkey.TriggerDown(button, down, vk => self._modifiers.TrackedDown(vk, false), IsKeyDown))
        {
            self._suppressedMouseButton = button;
            self.QueueToggle();
            return 1;
        }

        if (!injected && !down && button != 0 && button == self._suppressedMouseButton)
            return 1;
        if (!self.CaptureMouse || injected)
            return NativeMethods.CallNextHookEx(self._mouseHook, code, wParam, lParam);
        if (HookPolicy.Decide(true, false, false) == HookAction.Swallow)
            return 1;
        return NativeMethods.CallNextHookEx(self._mouseHook, code, wParam, lParam);
    }

    private bool Suppressed(byte button)
    {
        var virtualKey = button switch
        {
            0 => 0x01,
            1 => 0x02,
            2 => 0x04,
            3 => 0x05,
            4 => 0x06,
            _ => 0
        };
        return virtualKey != 0 && virtualKey == _suppressedMouseButton;
    }

    private void QueueToggle()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastToggle);
        if (now - last < 400)
            return;
        if (Interlocked.CompareExchange(ref _lastToggle, now, last) != last)
            return;
        ThreadPool.QueueUserWorkItem(_ => HotkeyPressed?.Invoke());
    }

    private void RequestHotkeyRefresh()
    {
        var hwnd = _hwnd;
        if (hwnd != 0)
            NativeMethods.PostMessage(hwnd, WmRefreshHotkey, 0, 0);
    }

    private void InstallHooks(nint instance)
    {
        _keyboardHook = NativeMethods.SetWindowsHookEx(KeyboardHookId, KeyboardProcedure, 0, 0);
        if (_keyboardHook == 0)
            _keyboardHook = NativeMethods.SetWindowsHookEx(KeyboardHookId, KeyboardProcedure, instance, 0);
        _mouseHook = NativeMethods.SetWindowsHookEx(MouseHookId, MouseProcedure, 0, 0);
        if (_mouseHook == 0)
            _mouseHook = NativeMethods.SetWindowsHookEx(MouseHookId, MouseProcedure, instance, 0);
    }

    private void RefreshRegisteredHotkey(nint hwnd)
    {
        NativeMethods.UnregisterHotKey(hwnd, HotkeyId);
        var hotkey = CurrentHotkey;
        var installed = HotkeyEnabled
            && hotkey.VirtualKey != 0
            && !hotkey.IsMouseButton
            && NativeMethods.RegisterHotKey(hwnd, HotkeyId, WinModifiers(hotkey), hotkey.VirtualKey);
        Volatile.Write(ref _systemHotkey, installed ? 1 : 0);
    }

    private static uint WinModifiers(Hotkey hotkey)
    {
        uint modifiers = 0x4000;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
            modifiers |= 0x0001;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control))
            modifiers |= 0x0002;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift))
            modifiers |= 0x0004;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Windows))
            modifiers |= 0x0008;
        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
