using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// =============================================================================
// CHEAPOverlay - Input Service (C#)
// Reads Instruments, broadcasts over WebSocket
// =============================================================================

class GHService
{
    // ── WebSocket server ──────────────────────────────────────────────────────
    static readonly List<WebSocket> Clients = new();
    static readonly Dictionary<WebSocket, SemaphoreSlim> SendLocks = new();
    static readonly object ClientLock = new();

    // ── Per-player state (slots 0–3) ──────────────────────────────────────────
    static GuitarState[] PlayerStates = new GuitarState[4] {
        new GuitarState { Player = 0 }, new GuitarState { Player = 1 },
        new GuitarState { Player = 2 }, new GuitarState { Player = 3 },
    };
    static readonly object[] StateLocks  = new object[4] { new(), new(), new(), new() };
    static bool[]             EverConnected = new bool[4];

    static async Task Main(string[] args)
    {
        Console.WriteLine("[CHEAPO] Starting on ws://localhost:2828");
        Console.WriteLine("[CHEAPO] Reading XInput + HID instruments");
        Console.WriteLine("[CHEAPO] Keep this window open while using the overlay.");
        // Start WebSocket server
        var wsTask = RunWebSocketServer();

        // Start guitar polling
        var xinputTask  = Task.Run(PollXInput);
        var hidTask     = Task.Run(PollHID);
        var soloTask    = Task.Run(PollSoloFrets);
        var micTask     = Task.Run(PollMic);
        var midiTask    = Task.Run(PollMIDI);
        var ghwtdeTask  = Task.Run(PollGHWTDE);
        var rb3dxTask   = Task.Run(PollRB3DX);
        var yargTask    = Task.Run(PollYARG);
        var ps2Task     = Task.Run(PollPS2);

        await Task.WhenAll(wsTask, xinputTask, hidTask, soloTask, micTask, midiTask, ghwtdeTask, rb3dxTask, yargTask, ps2Task);
    }

    // =========================================================================
    // WEBSOCKET SERVER
    // =========================================================================
    static async Task RunWebSocketServer()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:2828/");
        listener.Start();

        while (true)
        {
            var ctx = await listener.GetContextAsync();
            if (ctx.Request.IsWebSocketRequest)
            {
                _ = HandleClient(ctx);
            }
            else
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
            }
        }
    }

    static async Task HandleClient(HttpListenerContext ctx)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;

        lock (ClientLock) { Clients.Add(ws); SendLocks[ws] = new SemaphoreSlim(1, 1); }
        Console.WriteLine("[CHEAPO] Overlay connected");

        // Send current state for all players immediately
        for (int p = 0; p < 4; p++) await SendState(ws, PlayerStates[p]);

        // Keep alive until disconnected
        var buf = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch { }
        finally
        {
            lock (ClientLock) { Clients.Remove(ws); SendLocks.Remove(ws); }
            try { ws.Dispose(); } catch { }
        }
    }

    static async Task SendState(WebSocket ws, GuitarState state)
    {
        if (ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(state);
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
    }

    static void BroadcastPlayerState(int player, GuitarState state)
    {
        state.Player = player;
        BroadcastState(state);
    }

    static void BroadcastState(GuitarState state)
    {
        List<WebSocket> snapshot;
        lock (ClientLock) snapshot = new List<WebSocket>(Clients);
        var json = JsonSerializer.Serialize(state);
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var ws in snapshot)
        {
            if (ws.State != WebSocketState.Open) continue;
            SemaphoreSlim sem;
            lock (ClientLock) { if (!SendLocks.TryGetValue(ws, out sem)) continue; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                sem.Wait();
                try
                {
                    if (ws.State == WebSocketState.Open)
                        ws.SendAsync(new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text, true,
                            CancellationToken.None).GetAwaiter().GetResult();
                }
                catch { }
                finally { sem.Release(); }
            });
        }
    }

    // Sends a raw JSON string to all connected overlay clients (used for
    // non-GuitarState messages such as calibration state).
    static void BroadcastRaw(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        List<WebSocket> snapshot;
        lock (ClientLock) snapshot = new List<WebSocket>(Clients);
        foreach (var ws in snapshot)
        {
            if (ws.State != WebSocketState.Open) continue;
            SemaphoreSlim sem;
            lock (ClientLock) { if (!SendLocks.TryGetValue(ws, out sem)) continue; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                sem.Wait();
                try
                {
                    if (ws.State == WebSocketState.Open)
                        ws.SendAsync(new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text, true,
                            CancellationToken.None).GetAwaiter().GetResult();
                }
                catch { }
                finally { sem.Release(); }
            });
        }
    }

    static void UpdatePlayerState(int player, GuitarState newState)
    {
        newState.Player = player;
        // Once connected, never go back to disconnected
        if (EverConnected[player] && !newState.Connected) return;
        if (newState.Connected) EverConnected[player] = true;

        lock (StateLocks[player])
        {
            var cur = PlayerStates[player];
            newState.MouthOpen = cur.MouthOpen;
            newState.Kick     = cur.Kick;
            newState.DrumR    = cur.DrumR;
            newState.DrumY    = cur.DrumY;
            newState.DrumB    = cur.DrumB;
            newState.DrumG    = cur.DrumG;
            // Drum extras — managed by MIDI polling, always preserve here
            newState.DrumO    = cur.DrumO;
            newState.DrumYCym = cur.DrumYCym;
            newState.DrumBCym = cur.DrumBCym;
            newState.DrumGCym = cur.DrumGCym;
            newState.DrumMode = cur.DrumMode;

            // Solo frets are managed by UpdatePlayerSoloFrets — always preserve them here.
            newState.SoloG = cur.SoloG;
            newState.SoloR = cur.SoloR;
            newState.SoloY = cur.SoloY;
            newState.SoloB = cur.SoloB;
            newState.SoloO = cur.SoloO;

            // Star power is managed by PollGHWTDE — always preserve here.
            newState.StarPower = cur.StarPower;

            if (newState.Equals(cur)) return;
            PlayerStates[player] = newState;
        }
        BroadcastPlayerState(player, newState);
    }

    // Solo fret state is maintained independently and never overwritten by UpdatePlayerState.
    // Both PollHID (non-IG_ hasSolo devices) and PollSoloFrets (IG_ hasSolo devices)
    // call this to push changes.
    static void UpdatePlayerSoloFrets(int player, bool sg, bool sr, bool sy, bool sb, bool so)
    {
        lock (StateLocks[player])
        {
            var cur = PlayerStates[player];
            if (cur.SoloG == sg && cur.SoloR == sr &&
                cur.SoloY == sy && cur.SoloB == sb &&
                cur.SoloO == so) return;
            cur.SoloG = sg;
            cur.SoloR = sr;
            cur.SoloY = sy;
            cur.SoloB = sb;
            cur.SoloO = so;
            BroadcastPlayerState(player, cur);
        }
    }

    // =========================================================================
    // XINPUT PATH - reads Xbox 360 style instruments (Xplorer, Les Paul, etc.)
    // =========================================================================
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte   bLeftTrigger;
        public byte   bRightTrigger;
        public short  sThumbLX;
        public short  sThumbLY;
        public short  sThumbRX;
        public short  sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint          dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_CAPABILITIES
    {
        public byte   Type;
        public byte   SubType;
        public ushort Flags;
        public XINPUT_GAMEPAD  Gamepad;
        public XINPUT_VIBRATION Vibration;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern uint XInputGetState(uint dwUserIndex, ref XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
    static extern uint XInputGetCapabilities(uint dwUserIndex, uint dwFlags, ref XINPUT_CAPABILITIES pCapabilities);

    // XInput button constants
    const ushort XINPUT_DPAD_UP    = 0x0001;
    const ushort XINPUT_DPAD_DOWN  = 0x0002;
    const ushort XINPUT_BTN_A      = 0x1000; // Green fret / Green drum pad
    const ushort XINPUT_BTN_B      = 0x2000; // Red fret   / Red drum pad
    const ushort XINPUT_BTN_X      = 0x4000; // Blue fret  / Blue drum pad
    const ushort XINPUT_BTN_Y      = 0x8000; // Yellow fret/ Yellow drum pad
    const ushort XINPUT_BTN_LB     = 0x0100; // Orange fret / Kick pedal
    const ushort XINPUT_BTN_RB     = 0x0200; // (unused for guitars) / 2nd kick or extra pad

    // XInput device subtypes
    const byte XINPUT_DEVSUBTYPE_DRUM_KIT  = 0x08;
    const byte XINPUT_DEVSUBTYPE_KEYBOARD  = 0x0F;
    const byte XINPUT_DEVSUBTYPE_PRO_GUITAR = 0x19;


    static void PollXInput()
    {
        var prevPacket  = new uint[4];
        var prevBtns    = new ushort[4];
        // null = unknown; 0 = guitar/gamepad; 1 = drum kit; 2 = keyboard
        var slotType    = new byte?[4];

        Console.WriteLine("[XInput] polling started (controllers 0-3)");

        while (true)
        {
            for (uint i = 0; i < 4; i++)
            {
                var state = new XINPUT_STATE();
                uint result = XInputGetState(i, ref state);

                if (result != 0)
                {
                    if (slotType[i] != null) // slot was occupied, now disconnected
                    {
                        lock (StateLocks[i])
                        {
                            if (PlayerStates[i].Connected &&
                                PlayerStates[i].GuitarName?.StartsWith("XInput") == true)
                                UpdatePlayerState((int)i, new GuitarState { Connected = false });
                        }
                        slotType[i] = null;
                    }
                    prevBtns[i] = 0;
                    continue;
                }

                // First time we see this slot: check capabilities to identify device type
                if (slotType[i] == null)
                {
                    var caps = new XINPUT_CAPABILITIES();
                    if (XInputGetCapabilities(i, 0, ref caps) == 0)
                    {
                        if      (caps.SubType == XINPUT_DEVSUBTYPE_DRUM_KIT)   slotType[i] = 1;
                        else if (caps.SubType == XINPUT_DEVSUBTYPE_KEYBOARD)   slotType[i] = 2;
                        else if (caps.SubType == XINPUT_DEVSUBTYPE_PRO_GUITAR) slotType[i] = 3;
                        else                                                    slotType[i] = 0;
                        Console.WriteLine(
                            $"[XInput] slot {i}: SubType=0x{caps.SubType:X2} " +
                            $"→ {(slotType[i] == 1 ? "drum kit" : slotType[i] == 2 ? "keyboard" : slotType[i] == 3 ? "pro guitar" : "guitar/gamepad")}");
                    }
                    else
                    {
                        slotType[i] = 0; // can't identify → treat as guitar
                    }
                }

                if (slotType[i] == 1)
                {
                    // ── XInput drum kit ─────────────────────────────────────────
                    // Set drum mode + connected on first hit (or if mode changed)
                    lock (StateLocks[i])
                    {
                        var ps = PlayerStates[i];
                        if (!ps.Connected || ps.DrumMode != "rb")
                        {
                            EverConnected[i]        = true;
                            ps.Connected            = true;
                            ps.GuitarName           = $"XInput Drum Kit {i}";
                            ps.DrumMode             = "rb";
                            ps.InstrumentType       = "drums";
                            BroadcastPlayerState((int)i, ps);
                        }
                    }

                    // Only process rising edges so each hit is a discrete 120ms flash
                    if (state.dwPacketNumber == prevPacket[i]) continue;
                    prevPacket[i] = state.dwPacketNumber;

                    var gp = state.Gamepad;
                    ushort pressed = (ushort)(gp.wButtons & ~prevBtns[i]);
                    prevBtns[i] = gp.wButtons;

                    // RB drums: Green=A, Red=B, Yellow=Y, Blue=X, Kick=LB
                    if ((pressed & XINPUT_BTN_A)  != 0) FlashPlayerDrumPad((int)i, 'G');
                    if ((pressed & XINPUT_BTN_B)  != 0) FlashPlayerDrumPad((int)i, 'R');
                    if ((pressed & XINPUT_BTN_Y)  != 0) FlashPlayerDrumPad((int)i, 'Y');
                    if ((pressed & XINPUT_BTN_X)  != 0) FlashPlayerDrumPad((int)i, 'B');
                    if ((pressed & XINPUT_BTN_LB) != 0) FlashPlayerDrumPad((int)i, 'K');
                }
                else if (slotType[i] == 2)
                {
                    // ── XInput keyboard (RB3 keys) ───────────────────────────────
                    if (state.dwPacketNumber == prevPacket[i]) continue;
                    prevPacket[i] = state.dwPacketNumber;

                    var gp = state.Gamepad;

                    // Key bit positions (PlasticBand spec, 5-fret mapping):
                    // C2 = G : bRightTrigger bit 3 (0x08)
                    // D2 = R : bRightTrigger bit 1 (0x02)
                    // E2 = Y : sThumbLX bit 7 (0x0080)
                    // F2 = B : sThumbLX bit 6 (0x0040)
                    // G2 = O : sThumbLX bit 4 (0x0010)
                    bool g = (gp.bRightTrigger & 0x08) != 0;
                    bool r = (gp.bRightTrigger & 0x02) != 0;
                    bool y = ((ushort)gp.sThumbLX & 0x0080) != 0;
                    bool b = ((ushort)gp.sThumbLX & 0x0040) != 0;
                    bool o = ((ushort)gp.sThumbLX & 0x0010) != 0;

                    UpdatePlayerState((int)i, new GuitarState
                    {
                        Connected      = true,
                        GuitarName     = $"XInput Keyboard {i}",
                        InstrumentType = "keys",
                        G = g, R = r, Y = y, B = b, O = o,
                        Strum = "neutral"
                    });
                }
                else if (slotType[i] == 3)
                {
                    // ── XInput pro guitar (RB3 Mustang) ─────────────────────────
                    if (state.dwPacketNumber == prevPacket[i]) continue;
                    prevPacket[i] = state.dwPacketNumber;

                    var gp = state.Gamepad;

                    // Per-string fret numbers (5 bits each, 0=open, 1–17 = fret)
                    // Strings 1–3 (Low E, A, D) packed into combined triggers
                    // Strings 4–6 (G, B, High E) packed into sThumbLX
                    ushort trig = (ushort)(gp.bLeftTrigger | (gp.bRightTrigger << 8));
                    ushort lx   = (ushort)gp.sThumbLX;

                    int fLowE  = trig & 0x1F;
                    int fA     = (trig >> 5)  & 0x1F;
                    int fD     = (trig >> 10) & 0x1F;
                    int fG     = lx   & 0x1F;
                    int fB     = (lx   >> 5)  & 0x1F;
                    int fHighE = (lx   >> 10) & 0x1F;

                    // Frets 13–17 are the solo zone (upper neck, same as solo buttons
                    // on a standard RB guitar).  Any string there = solo mode.
                    bool anySolo = (fLowE  >= 13 && fLowE  <= 17) ||
                                   (fA     >= 13 && fA     <= 17) ||
                                   (fD     >= 13 && fD     <= 17) ||
                                   (fG     >= 13 && fG     <= 17) ||
                                   (fB     >= 13 && fB     <= 17) ||
                                   (fHighE >= 13 && fHighE <= 17);

                    // 5-fret color flags — high bits of velocity byte pairs
                    // Green  = sThumbLY  bit 7  (0x0080)
                    // Red    = sThumbLY  bit 15 (0x8000)
                    // Yellow = sThumbRX  bit 7  (0x0080)
                    // Blue   = sThumbRX  bit 15 (0x8000)
                    // Orange = sThumbRY  bit 7  (0x0080)
                    bool g = ((ushort)gp.sThumbLY & 0x0080) != 0;
                    bool r = ((ushort)gp.sThumbLY & 0x8000) != 0;
                    bool y = ((ushort)gp.sThumbRX & 0x0080) != 0;
                    bool b = ((ushort)gp.sThumbRX & 0x8000) != 0;
                    bool o = ((ushort)gp.sThumbRY & 0x0080) != 0;

                    // Strum: D-pad up/down
                    string strum = "neutral";
                    if ((gp.wButtons & XINPUT_DPAD_UP)   != 0) strum = "up";
                    if ((gp.wButtons & XINPUT_DPAD_DOWN) != 0) strum = "down";

                    // Route color flags: solo zone → solo frets, regular zone → frets
                    UpdatePlayerState((int)i, new GuitarState
                    {
                        Connected      = true,
                        GuitarName     = $"XInput Pro Guitar {i}",
                        InstrumentType = "guitar",
                        G = !anySolo && g,
                        R = !anySolo && r,
                        Y = !anySolo && y,
                        B = !anySolo && b,
                        O = !anySolo && o,
                        Strum = strum
                    });
                    UpdatePlayerSoloFrets((int)i,
                        anySolo && g,
                        anySolo && r,
                        anySolo && y,
                        anySolo && b,
                        anySolo && o);
                }
                else
                {
                    // ── XInput guitar / gamepad ──────────────────────────────────
                    // Packet number not used — some controllers (e.g. Santroller)
                    // don't increment it reliably. Use button-state change instead.
                    var gp = state.Gamepad;
                    if (gp.wButtons == prevBtns[i] && state.dwPacketNumber == prevPacket[i]) continue;
                    prevPacket[i] = state.dwPacketNumber;
                    prevBtns[i]   = gp.wButtons;

                    // Fret mapping: Green=A, Red=B, Yellow=Y, Blue=X, Orange=LB
                    bool g = (gp.wButtons & XINPUT_BTN_A)  != 0;
                    bool r = (gp.wButtons & XINPUT_BTN_B)  != 0;
                    bool y = (gp.wButtons & XINPUT_BTN_Y)  != 0;
                    bool b = (gp.wButtons & XINPUT_BTN_X)  != 0;
                    bool o = (gp.wButtons & XINPUT_BTN_LB) != 0;

                    // Strum: D-pad up/down, or left stick Y
                    string strum = "neutral";
                    if ((gp.wButtons & XINPUT_DPAD_UP)   != 0) strum = "up";
                    if ((gp.wButtons & XINPUT_DPAD_DOWN)  != 0) strum = "down";
                    if (strum == "neutral" && gp.sThumbLY < -20000) strum = "down";
                    if (strum == "neutral" && gp.sThumbLY >  20000) strum = "up";

                    UpdatePlayerState((int)i, new GuitarState
                    {
                        Connected      = true,
                        GuitarName     = $"XInput Controller {i}",
                        InstrumentType = "guitar",
                        G = g, R = r, Y = y, B = b, O = o,
                        Strum = strum
                    });
                }
            }

            Thread.Sleep(4); // ~250hz polling
        }
    }

    // Shared by XInput drum path and MIDI path — flash a drum pad for 120 ms
    static void FlashPlayerDrumPad(int player, char pad)
    {
        long now = Environment.TickCount64;
        lock (StateLocks[player]) {
            _drumHitTime[player][pad] = now;
            SetPlayerDrumPad(player, pad, true);
            BroadcastPlayerState(player, PlayerStates[player]);
        }
        ThreadPool.QueueUserWorkItem(_ => {
            Thread.Sleep(120);
            lock (StateLocks[player]) {
                if (_drumHitTime[player].TryGetValue(pad, out long t) && Environment.TickCount64 - t >= 120) {
                    SetPlayerDrumPad(player, pad, false);
                    BroadcastPlayerState(player, PlayerStates[player]);
                }
            }
        });
    }

    // =========================================================================
    // HID PATH - reads HID guitars (Santroller, RCM, RB wireless)
    // =========================================================================

    // Known guitar VID/PIDs
    static readonly (int vid, int pid, bool hasSolo, string name)[] KnownGuitars = {
        (0x1430, 0x4748, false, "GH Xplorer"),
        (0x1430, 0x474C, false, "GH Les Paul"),
        (0x1430, 0x4750, false, "GH World Tour"),
        (0x1BAD, 0x0002, true,  "RB1/RB2 Guitar"),
        (0x1BAD, 0x02AA, true,  "RB2 Guitar (wireless)"),
        (0x1BAD, 0x0203, true,  "RB3 Guitar"),
        (0x1BAD, 0x0004, true,  "RB4 Guitar"),
    };

    [DllImport("hid.dll")]
    static extern bool HidD_GetHidGuid(out Guid HidGuid);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string Enumerator,
        IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet,
        IntPtr DeviceInfoData, ref Guid InterfaceClassGuid,
        uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("hid.dll")]
    static extern bool HidD_GetAttributes(IntPtr HidDeviceObject,
        ref HIDD_ATTRIBUTES Attributes);

    [DllImport("hid.dll", CharSet = CharSet.Auto)]
    static extern bool HidD_GetProductString(IntPtr HidDeviceObject,
        byte[] Buffer, uint BufferLength);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public uint   Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    const uint DIGCF_PRESENT         = 0x02;
    const uint DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ          = 0x80000000;
    const uint FILE_SHARE_READ       = 0x01;
    const uint FILE_SHARE_WRITE      = 0x02;
    const uint OPEN_EXISTING         = 3;
    const uint FILE_FLAG_OVERLAPPED  = 0x40000000;
    static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    static void PollHID()
    {
        Console.WriteLine("[HID] polling started");

        while (true)
        {
            // Don't compete with XInput player 0 if it has an active guitar
            bool xinputActive;
            lock (StateLocks[0])
                xinputActive = PlayerStates[0].Connected &&
                               PlayerStates[0].GuitarName?.StartsWith("XInput") == true;

            if (xinputActive)
            {
                Thread.Sleep(500);
                continue;
            }

            IntPtr handle = INVALID_HANDLE_VALUE;
            string guitarName = null;
            bool hasSolo = false;

            try
            {
                HidD_GetHidGuid(out Guid hidGuid);
                var devInfoSet = SetupDiGetClassDevs(ref hidGuid, null,
                    IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

                if (devInfoSet == INVALID_HANDLE_VALUE)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                uint idx = 0;
                var devIfaceData = new SP_DEVICE_INTERFACE_DATA();
                devIfaceData.cbSize = (uint)Marshal.SizeOf(devIfaceData);

                while (SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero,
                    ref hidGuid, idx++, ref devIfaceData))
                {
                    SetupDiGetDeviceInterfaceDetail(devInfoSet, ref devIfaceData,
                        IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);

                    if (reqSize == 0) continue;

                    var detailBuf = Marshal.AllocHGlobal((int)reqSize);
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);

                    SetupDiGetDeviceInterfaceDetail(devInfoSet, ref devIfaceData,
                        detailBuf, reqSize, out _, IntPtr.Zero);

                    string path = Marshal.PtrToStringAuto(
                        IntPtr.Add(detailBuf, 4));
                    Marshal.FreeHGlobal(detailBuf);

                    if (string.IsNullOrEmpty(path)) continue;

                    // Skip XInput interfaces
                    if (path.ToUpper().Contains("IG_")) continue;

                    var tmpHandle = CreateFile(path,
                        GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                    if (tmpHandle == INVALID_HANDLE_VALUE) continue;

                    var attrs = new HIDD_ATTRIBUTES();
                    attrs.Size = (uint)Marshal.SizeOf(attrs);

                    if (!HidD_GetAttributes(tmpHandle, ref attrs))
                    {
                        CloseHandle(tmpHandle);
                        continue;
                    }

                    int vid = attrs.VendorID;
                    int pid = attrs.ProductID;

                    bool matched = false;
                    foreach (var (kv, kp, ks, kn) in KnownGuitars)
                    {
                        if (kv == vid && kp == pid)
                        {
                            handle = tmpHandle;
                            guitarName = kn;
                            hasSolo = ks;
                            matched = true;
                            Console.WriteLine($"[HID] found: {kn} ({vid:X4}:{pid:X4})");
                            break;
                        }
                    }

                    if (!matched)
                    {
                        CloseHandle(tmpHandle);
                    }
                    else
                    {
                        Console.WriteLine($"[HID] matched: {guitarName} path: {path}");
                        break;
                    }
                }

                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HID] scan error: {ex.Message}");
                Thread.Sleep(2000);
                continue;
            }

            if (handle == INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            {
                Thread.Sleep(2000);
                continue;
            }

            // Read loop
            UpdatePlayerState(0, new GuitarState { Connected = true, GuitarName = guitarName, InstrumentType = "guitar" });
            var buf = new byte[64];

            while (true)
            {
                if (!ReadFile(handle, buf, (uint)buf.Length, out uint bytesRead, IntPtr.Zero)
                    || bytesRead == 0)
                {
                    var err = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[HID] read failed (error {err}), reconnecting...");
                    break;
                }

                int regularFrets = buf[6];
                // buf[7] contains combined fret state (regular + solo); mask out regular to isolate solo-only bits
                int soloFrets    = hasSolo ? (buf[7] & ~regularFrets) : 0;
                // Keep regular and solo frets separate — solo frets use their own sprites
                int strum        = buf[8];

                UpdatePlayerState(0, new GuitarState
                {
                    Connected      = true,
                    GuitarName     = guitarName,
                    InstrumentType = "guitar",
                    G    = (regularFrets & 0x01) != 0,
                    R    = (regularFrets & 0x02) != 0,
                    Y    = (regularFrets & 0x08) != 0,
                    B    = (regularFrets & 0x04) != 0,
                    O    = (regularFrets & 0x10) != 0,
                    Strum = strum == 1 ? "up" : strum == 5 ? "down" : "neutral",
                });
                if (hasSolo)
                    UpdatePlayerSoloFrets(0,
                        (soloFrets & 0x01) != 0, (soloFrets & 0x02) != 0,
                        (soloFrets & 0x08) != 0, (soloFrets & 0x04) != 0,
                        (soloFrets & 0x10) != 0);
            }

            CloseHandle(handle);
            UpdatePlayerState(0, new GuitarState { Connected = false });
            Thread.Sleep(2000);
        }
    }

    // =========================================================================
    // SOLO FRET POLLING — reads buf[7] from IG_ HID paths (XInput RB guitars)
    // XInput exposes raw HID reports on the IG_ path alongside its own API.
    // PollHID skips IG_ paths, so this task handles solo frets for those devices.
    // Non-IG_ hasSolo devices are handled directly inside PollHID.
    // =========================================================================
    static void PollSoloFrets()
    {
        Console.WriteLine("[Solo Frets] poller started");

        while (true)
        {
            IntPtr handle = INVALID_HANDLE_VALUE;

            try
            {
                HidD_GetHidGuid(out Guid hidGuid);
                var devInfoSet = SetupDiGetClassDevs(ref hidGuid, null,
                    IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

                if (devInfoSet == INVALID_HANDLE_VALUE) { Thread.Sleep(2000); continue; }

                uint idx = 0;
                var devIfaceData = new SP_DEVICE_INTERFACE_DATA();
                devIfaceData.cbSize = (uint)Marshal.SizeOf(devIfaceData);

                while (SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero,
                    ref hidGuid, idx++, ref devIfaceData))
                {
                    SetupDiGetDeviceInterfaceDetail(devInfoSet, ref devIfaceData,
                        IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;

                    var detailBuf = Marshal.AllocHGlobal((int)reqSize);
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    SetupDiGetDeviceInterfaceDetail(devInfoSet, ref devIfaceData,
                        detailBuf, reqSize, out _, IntPtr.Zero);

                    string path = Marshal.PtrToStringAuto(IntPtr.Add(detailBuf, 4));
                    Marshal.FreeHGlobal(detailBuf);

                    if (string.IsNullOrEmpty(path)) continue;

                    // We ONLY handle XInput HID paths (IG_) here.
                    // Non-IG_ hasSolo devices are handled by PollHID.
                    if (!path.ToUpper().Contains("IG_")) continue;

                    var tmpHandle = CreateFile(path,
                        GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (tmpHandle == INVALID_HANDLE_VALUE) continue;

                    var attrs = new HIDD_ATTRIBUTES();
                    attrs.Size = (uint)Marshal.SizeOf(attrs);
                    if (!HidD_GetAttributes(tmpHandle, ref attrs))
                    { CloseHandle(tmpHandle); continue; }

                    int vid = attrs.VendorID;
                    int pid = attrs.ProductID;

                    bool matched = false;
                    foreach (var (kv, kp, ks, kn) in KnownGuitars)
                    {
                        if (kv == vid && kp == pid && ks) // hasSolo only
                        {
                            handle = tmpHandle;
                            matched = true;
                            Console.WriteLine($"[Solo Frets] reader: {kn} ({vid:X4}:{pid:X4})");
                            break;
                        }
                    }
                    if (!matched) CloseHandle(tmpHandle);
                    else break;
                }

                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Solo Frets] scan error: {ex.Message}");
                Thread.Sleep(2000);
                continue;
            }

            if (handle == INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            {
                Thread.Sleep(2000);
                continue;
            }

            // Read loop — buf[8]=0x01: solo frets active, buf[7] has the fret bits.
            //             buf[8]=0x00: regular frets active, solo frets cleared.
            var buf = new byte[64];
            while (true)
            {
                if (!ReadFile(handle, buf, (uint)buf.Length, out uint bytesRead, IntPtr.Zero)
                    || bytesRead == 0)
                {
                    Console.WriteLine("[Solo Frets] reader disconnected, rescanning...");
                    UpdatePlayerSoloFrets(0, false, false, false, false, false);
                    break;
                }

                int fretBits = buf[8] == 0x01 ? buf[7] : 0;
                UpdatePlayerSoloFrets(0,
                    (fretBits & 0x01) != 0, (fretBits & 0x02) != 0,
                    (fretBits & 0x08) != 0, (fretBits & 0x04) != 0,
                    (fretBits & 0x10) != 0);
            }

            CloseHandle(handle);
            Thread.Sleep(2000);
        }
    }

    // =========================================================================
    // MIC POLLING — WaveIn (captures PCM) + RNNoise voice activity detection
    // =========================================================================

    // ── RNNoise P/Invoke ──────────────────────────────────────────────────────
    // Native DLL (rnnoise.dll) is bundled via YellowDogMan.RNNoise.NET NuGet.
    // Returns voice probability 0–1 per 480-sample frame at 48 kHz.
    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr rnnoise_create(IntPtr model);
    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
    static extern void rnnoise_destroy(IntPtr st);
    [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
    static extern float rnnoise_process_frame(IntPtr st, float[] pcmOut, float[] pcmIn);

    const int   RNNOISE_FRAME_SIZE = 480;   // 10 ms at 48 kHz
    const float MIC_VOICE_PROB     = 0.80f; // voice probability to open mouth (0–1)

    // ── Fallback amplitude gate (used if rnnoise.dll is missing) ─────────────
    const float MIC_THRESHOLD   = 0.01f;
    const float MIC_CEILING     = 0.45f;
    const float MIC_EMA_ALPHA   = 0.30f;
    const int   MIC_HOLD_FRAMES = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX_MIC {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint   nSamplesPerSec;
        public uint   nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR {
        public IntPtr lpData;
        public uint   dwBufferLength;
        public uint   dwBytesRecorded;
        public IntPtr dwUser;
        public uint   dwFlags;
        public uint   dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    const uint WHDR_DONE = 0x00000001;

    [DllImport("winmm.dll")] static extern uint waveInOpen(out IntPtr hwi, uint dev, ref WAVEFORMATEX_MIC fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern uint waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cb);
    [DllImport("winmm.dll")] static extern uint waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cb);
    [DllImport("winmm.dll")] static extern uint waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")] static extern uint waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")] static extern uint waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cb);
    [DllImport("winmm.dll")] static extern uint waveInClose(IntPtr hwi);

    static float ComputePeak16(IntPtr buf, int bytes)
    {
        float peak = 0f;
        for (int i = 0; i < bytes / 2; i++)
        {
            float v = Math.Abs((int)Marshal.ReadInt16(buf, i * 2)) / 32768f;
            if (v > peak) peak = v;
        }
        return peak;
    }

    static void UpdateMouth(bool open)
    {
        lock (StateLocks[0])
        {
            if (PlayerStates[0].MouthOpen == open) return;
            PlayerStates[0].MouthOpen = open;
            BroadcastPlayerState(0, PlayerStates[0]);
        }
    }

    static void PollMic()
    {
        Console.WriteLine("[Mic] polling started");
        const uint RATE      = 48000;               // RNNoise requires 48 kHz
        const int  BUF_BYTES = (int)(RATE / 10 * 2); // 100 ms, mono, 16-bit = 9600 bytes

        // ── Init RNNoise (once, lives for process lifetime) ───────────────────
        IntPtr rnState = IntPtr.Zero;
        var    rnIn    = new float[RNNOISE_FRAME_SIZE];
        var    rnOut   = new float[RNNOISE_FRAME_SIZE];
        try
        {
            rnState = rnnoise_create(IntPtr.Zero);
            if (rnState != IntPtr.Zero)
                Console.WriteLine("[Mic] voice detection active");
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine("[Mic].dll not found — falling back to amplitude gate");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Mic] init failed: {ex.Message} — falling back");
        }
        bool useRnnoise = rnState != IntPtr.Zero;

        uint hdrSz = (uint)Marshal.SizeOf<WAVEHDR>();

        while (true)
        {
            IntPtr hwi  = IntPtr.Zero;
            IntPtr buf1 = IntPtr.Zero, buf2 = IntPtr.Zero;
            IntPtr hdr1 = IntPtr.Zero, hdr2 = IntPtr.Zero;
            try
            {
                var fmt = new WAVEFORMATEX_MIC {
                    wFormatTag      = 1, // PCM
                    nChannels       = 1,
                    nSamplesPerSec  = RATE,
                    nAvgBytesPerSec = RATE * 2,
                    nBlockAlign     = 2,
                    wBitsPerSample  = 16,
                    cbSize          = 0
                };

                uint mmr = waveInOpen(out hwi, uint.MaxValue /*WAVE_MAPPER*/, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
                if (mmr != 0) throw new Exception($"waveInOpen failed: {mmr}");

                buf1 = Marshal.AllocHGlobal(BUF_BYTES);
                buf2 = Marshal.AllocHGlobal(BUF_BYTES);
                hdr1 = Marshal.AllocHGlobal((int)hdrSz);
                hdr2 = Marshal.AllocHGlobal((int)hdrSz);

                void InitHdr(IntPtr hdr, IntPtr buf) {
                    for (int i = 0; i < (int)hdrSz; i++) Marshal.WriteByte(hdr, i, 0);
                    Marshal.StructureToPtr(new WAVEHDR { lpData = buf, dwBufferLength = BUF_BYTES }, hdr, false);
                }

                InitHdr(hdr1, buf1); InitHdr(hdr2, buf2);
                waveInPrepareHeader(hwi, hdr1, hdrSz); waveInPrepareHeader(hwi, hdr2, hdrSz);
                waveInAddBuffer(hwi, hdr1, hdrSz);     waveInAddBuffer(hwi, hdr2, hdrSz);
                waveInStart(hwi);
                Console.WriteLine("[Mic] ready");

                float ema         = 0f;
                int   framesInRange = 0;

                while (true)
                {
                    Thread.Sleep(16);
                    foreach (var (hdr, buf) in new[] { (hdr1, buf1), (hdr2, buf2) })
                    {
                        var w = Marshal.PtrToStructure<WAVEHDR>(hdr);
                        if ((w.dwFlags & WHDR_DONE) == 0) continue;

                        if (useRnnoise)
                        {
                            // Feed 480-sample frames into RNNoise, take highest voice probability
                            int   samples = (int)(w.dwBytesRecorded / 2);
                            float maxProb = 0f;
                            int   offset  = 0;
                            while (offset + RNNOISE_FRAME_SIZE <= samples)
                            {
                                for (int j = 0; j < RNNOISE_FRAME_SIZE; j++)
                                    rnIn[j] = Marshal.ReadInt16(buf, (offset + j) * 2); // [-32768, 32767]
                                float prob = rnnoise_process_frame(rnState, rnOut, rnIn);
                                if (prob > maxProb) maxProb = prob;
                                offset += RNNOISE_FRAME_SIZE;
                            }
                            UpdateMouth(maxProb > MIC_VOICE_PROB);
                        }
                        else
                        {
                            // Fallback: amplitude threshold + ceiling
                            float peak = ComputePeak16(buf, (int)w.dwBytesRecorded);
                            ema = ema * (1f - MIC_EMA_ALPHA) + peak * MIC_EMA_ALPHA;
                            bool inRange = ema > MIC_THRESHOLD && ema < MIC_CEILING;
                            framesInRange = inRange ? framesInRange + 1 : 0;
                            UpdateMouth(framesInRange >= MIC_HOLD_FRAMES);
                        }

                        waveInUnprepareHeader(hwi, hdr, hdrSz);
                        Marshal.StructureToPtr(new WAVEHDR { lpData = buf, dwBufferLength = BUF_BYTES }, hdr, false);
                        waveInPrepareHeader(hwi, hdr, hdrSz);
                        waveInAddBuffer(hwi, hdr, hdrSz);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mic] unavailable: {ex.Message}");
                Console.WriteLine("[Mic] Retrying in 5s...");
            }
            finally
            {
                if (hwi != IntPtr.Zero) { waveInReset(hwi); waveInClose(hwi); }
                if (hdr1 != IntPtr.Zero) Marshal.FreeHGlobal(hdr1);
                if (hdr2 != IntPtr.Zero) Marshal.FreeHGlobal(hdr2);
                if (buf1 != IntPtr.Zero) Marshal.FreeHGlobal(buf1);
                if (buf2 != IntPtr.Zero) Marshal.FreeHGlobal(buf2);
            }
            Thread.Sleep(5000);
        }
    }

    // =========================================================================
    // MIDI DRUMS — MIDI Pro Adapter, RB3 pro drums, generic e-kits
    // =========================================================================
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MIDIINCAPS {
        public ushort wMid, wPid;
        public uint   vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint   dwSupport;
    }

    delegate void MidiInProc(IntPtr hMidi, uint msg, IntPtr inst, IntPtr p1, IntPtr p2);

    [DllImport("winmm.dll")] static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    static extern uint midiInGetDevCaps(uint dev, ref MIDIINCAPS caps, uint sz);
    [DllImport("winmm.dll")]
    static extern uint midiInOpen(out IntPtr lph, uint dev, MidiInProc cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern uint midiInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern uint midiInClose(IntPtr h);

    const uint MIM_DATA          = 0x3C3;
    const uint CALLBACK_FUNCTION = 0x00030000;

    static MidiInProc              _midiCb;
    static readonly Dictionary<char, long>[] _drumHitTime = new Dictionary<char, long>[4]
        { new(), new(), new(), new() };
    static readonly List<IntPtr>           _midiHandles  = new();
    static readonly object                 _midiLock     = new();

    // GH mode: 5 pads + kick, no cymbal/tom distinction
    static char NoteToGHPad(byte note) => note switch {
        33 or 35 or 36 => 'K', // kick
        38 or 40       => 'R', // red / snare
        48 or 50       => 'Y', // yellow pad
        45 or 47       => 'B', // blue pad
        41 or 43       => 'G', // green pad
        44 or 49 or 51 => 'O', // orange cymbal
        _              => '\0'
    };

    // RB mode: lowercase = cymbal, uppercase = tom
    static char NoteToRBPad(byte note) => note switch {
        33 or 35 or 36       => 'K', // kick
        38 or 40             => 'R', // red / snare (always a tom in RB)
        22 or 26 or 42 or 46 => 'y', // yellow cymbal (hi-hat)
        48 or 50             => 'Y', // yellow tom
        44 or 49             => 'b', // blue cymbal
        45 or 47             => 'B', // blue tom
        51 or 55 or 57 or 59 => 'g', // green cymbal (ride)
        41 or 43             => 'G', // green tom
        _                    => '\0'
    };

    static char NoteTodrumPad(byte note) =>
        PlayerStates[0].DrumMode == "gh" ? NoteToGHPad(note) : NoteToRBPad(note);

    static void SetPlayerDrumPad(int player, char pad, bool v)
    {
        var ps = PlayerStates[player];
        switch (pad) {
            case 'K': ps.Kick     = v; break;
            case 'R': ps.DrumR    = v; break;
            case 'Y': ps.DrumY    = v; break;
            case 'B': ps.DrumB    = v; break;
            case 'G': ps.DrumG    = v; break;
            case 'O': ps.DrumO    = v; break; // GH orange cymbal
            case 'y': ps.DrumYCym = v; break; // RB yellow cymbal
            case 'b': ps.DrumBCym = v; break; // RB blue cymbal
            case 'g': ps.DrumGCym = v; break; // RB green cymbal
        }
    }

    static void OnMidiMessage(IntPtr hMidi, uint wMsg, IntPtr inst, IntPtr p1, IntPtr p2)
    {
        if (wMsg != MIM_DATA) return;
        uint raw  = (uint)p1;
        byte stat = (byte)(raw & 0xFF);
        byte note = (byte)((raw >> 8) & 0xFF);
        byte vel  = (byte)((raw >> 16) & 0xFF);

        // Drum pads — note-on only
        if ((stat & 0xF0) != 0x90 || vel == 0) return;

        char pad = NoteTodrumPad(note);
        if (pad == '\0') return;

        FlashPlayerDrumPad(0, pad);
    }

    static bool IsGHDrumName(string name) =>
        name.IndexOf("GH",           StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf("Guitar Hero",  StringComparison.OrdinalIgnoreCase) >= 0;

    static void PollMIDI()
    {
        Console.WriteLine("[MIDI] polling started");
        _midiCb = OnMidiMessage; // pin delegate — must not be GC'd
        var openIdx = new HashSet<uint>();

        while (true)
        {
            uint n = midiInGetNumDevs();

            if (n < openIdx.Count) // device removed — close all and rescan
            {
                lock (_midiLock) {
                    foreach (var h in _midiHandles) try { midiInClose(h); } catch { }
                    _midiHandles.Clear();
                }
                openIdx.Clear();
                // Clear drum mode when all MIDI devices disconnect
                lock (StateLocks[0]) {
                    if (!string.IsNullOrEmpty(PlayerStates[0].DrumMode)) {
                        PlayerStates[0].DrumMode       = "";
                        PlayerStates[0].InstrumentType = "guitar";
                        BroadcastPlayerState(0, PlayerStates[0]);
                    }
                }
                Console.WriteLine("[MIDI] device removed, rescanning");
            }

            for (uint i = 0; i < n; i++)
            {
                if (openIdx.Contains(i)) continue;
                var caps = new MIDIINCAPS();
                midiInGetDevCaps(i, ref caps, (uint)Marshal.SizeOf<MIDIINCAPS>());
                if (midiInOpen(out IntPtr h, i, _midiCb, IntPtr.Zero, CALLBACK_FUNCTION) != 0) continue;
                midiInStart(h);
                openIdx.Add(i);
                lock (_midiLock) _midiHandles.Add(h);
                Console.WriteLine($"[MIDI] opened: {caps.szPname}");

                // Set drum mode: GH name wins; first device sets RB if no GH seen yet
                bool isGH = IsGHDrumName(caps.szPname);
                lock (StateLocks[0]) {
                    if (isGH || string.IsNullOrEmpty(PlayerStates[0].DrumMode)) {
                        string newMode = isGH ? "gh" : "rb";
                        if (PlayerStates[0].DrumMode != newMode) {
                            PlayerStates[0].DrumMode       = newMode;
                            PlayerStates[0].InstrumentType = "drums";
                            Console.WriteLine($"[MIDI] mode set to '{newMode}' (device: {caps.szPname})");
                            BroadcastPlayerState(0, PlayerStates[0]);
                        }
                    }
                }
            }

            Thread.Sleep(2000);
        }
    }

    // =========================================================================
    // PS2 ADAPTER SUPPORT                                       ⚠ UNTESTED ⚠
    //
    // Detects PS2-to-USB HID adapters by product name / manufacturer VID.
    // If a matching device is found with no saved calibration, the overlay is
    // locked into a full-screen calibration wizard that walks the user through
    // pressing each fret, strum up/down, and star power.  The mapping is
    // saved to calibration.json alongside the service exe and reused on
    // subsequent connections.
    //
    // Player slot: first available (lowest index not already connected).
    //
    // Why untested: PS2 adapter HID report layouts vary wildly between
    // manufacturers and even firmware revisions of the same adapter.
    // The calibration logic makes no assumptions about report structure,
    // but the detection heuristics and baseline-diffing algorithm have not
    // been exercised against real hardware.
    // =========================================================================

    // Name fragments that suggest a generic PS2/USB adapter.
    static readonly string[] PS2_ADAPTER_PATTERNS =
    {
        "DragonRise", "GreenAsia", "Twin USB", "Dual USB",
        "USB Gamepad", "USB Joystick", "USB Game Controller",
        "SHANWAN", "2Axes", "USB,2-axis", "Joypad to USB",
        "PC Game Pad", "PSX to USB", "PS2 Adapter", "PS2 to USB",
    };

    // VIDs of well-known PS2 adapter chipset manufacturers.
    static readonly ushort[] PS2_ADAPTER_VIDS = { 0x0810, 0x0E8F, 0x0079, 0x0583, 0x11FF };

    // Strings that disqualify a device from being treated as a PS2 adapter.
    static readonly string[] PS2_EXCLUSIONS =
    {
        "Xbox", "XBox", "Wireless Receiver", "Switch",
        "DualShock", "DualSense", "PlayStation 4", "PlayStation 5",
        "Santroller", "RCM",
    };

    // Checks whether a HID device (by product name + VID) looks like a PS2 adapter
    // that isn't already in KnownGuitars.
    static bool IsPS2Adapter(string productName, ushort vid)
    {
        var upper = (productName ?? "").ToUpperInvariant();
        foreach (var x in PS2_EXCLUSIONS)
            if (upper.Contains(x.ToUpperInvariant())) return false;
        foreach (var v in PS2_ADAPTER_VIDS)
            if (vid == v) return true;
        foreach (var p in PS2_ADAPTER_PATTERNS)
            if (upper.Contains(p.ToUpperInvariant())) return true;
        return false;
    }

    // Returns the first player slot (0-3) that is currently not connected.
    static int FindFreePlayerSlot()
    {
        for (int i = 0; i < 4; i++)
            lock (StateLocks[i])
                if (!PlayerStates[i].Connected) return i;
        return 0;
    }

    // ── Calibration data model ─────────────────────────────────────────────────
    // One button/axis entry: button is pressed when (report[Byte] & Mask) == Value.
    class PS2InputEntry
    {
        public int  Byte  { get; set; }
        public byte Mask  { get; set; }
        public byte Value { get; set; }
        public bool IsValid => Mask != 0;
    }

    class PS2Calibration
    {
        public string       Name      { get; set; } = "";
        public PS2InputEntry Green     { get; set; } = new();
        public PS2InputEntry Red       { get; set; } = new();
        public PS2InputEntry Yellow    { get; set; } = new();
        public PS2InputEntry Blue      { get; set; } = new();
        public PS2InputEntry Orange    { get; set; } = new();
        public PS2InputEntry StrumUp   { get; set; } = new();
        public PS2InputEntry StrumDown { get; set; } = new();
        public PS2InputEntry StarPower { get; set; } = new();
    }

    static string CalibrationPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "calibration.json");

    static Dictionary<string, PS2Calibration> LoadCalibrations()
    {
        try
        {
            if (!System.IO.File.Exists(CalibrationPath)) return new();
            var json = System.IO.File.ReadAllText(CalibrationPath);
            return JsonSerializer.Deserialize<Dictionary<string, PS2Calibration>>(json) ?? new();
        }
        catch { return new(); }
    }

    static void SaveCalibrations(Dictionary<string, PS2Calibration> cals)
    {
        try
        {
            var json = JsonSerializer.Serialize(cals,
                new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(CalibrationPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PS2] Failed to save calibration: {ex.Message}");
        }
    }

    // Sends calibration state to all overlay clients.
    // active=true: overlay locks into calibration wizard showing 'step'/'label'.
    // active=false: overlay resumes normal display.
    static void BroadcastCalibrationState(bool active,
        string step = "", string label = "", string device = "")
    {
        string json = active
            ? $"{{\"calibrating\":true,\"step\":\"{step}\"," +
              $"\"label\":\"{JsonEncodedText.Encode(label)}\"," +
              $"\"device\":\"{JsonEncodedText.Encode(device)}\"}}"
            : "{\"calibrating\":false}";
        BroadcastRaw(json);
    }

    // ── HID calibration helpers ────────────────────────────────────────────────

    // Reads until N consecutive identical reports are seen; returns that report.
    static byte[]? ReadStableReport(IntPtr handle, byte[] buf, int stableCount, int timeoutMs = 5000)
    {
        byte[]? last = null; int consecutive = 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!ReadFile(handle, buf, (uint)buf.Length, out uint n, IntPtr.Zero) || n == 0)
            { Thread.Sleep(10); consecutive = 0; last = null; continue; }
            var copy = buf[..(int)n];
            if (last != null && copy.SequenceEqual(last))
            { if (++consecutive >= stableCount) return copy; }
            else { consecutive = 1; last = copy; }
            Thread.Sleep(4);
        }
        return null;
    }

    // Waits until N consecutive identical reports that differ from baseline.
    static byte[]? WaitForDiff(IntPtr handle, byte[] buf,
        byte[] baseline, int stableCount, int timeoutMs)
    {
        byte[]? last = null; int consecutive = 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!ReadFile(handle, buf, (uint)buf.Length, out uint n, IntPtr.Zero) || n == 0)
            { Thread.Sleep(10); continue; }
            var copy = buf[..(int)n];
            int cmp = Math.Min(baseline.Length, copy.Length);
            bool differs = false;
            for (int i = 0; i < cmp; i++) if (copy[i] != baseline[i]) { differs = true; break; }
            if (differs)
            {
                if (last != null && copy.SequenceEqual(last))
                { if (++consecutive >= stableCount) return copy; }
                else { consecutive = 1; last = copy; }
            }
            else { consecutive = 0; last = null; }
            Thread.Sleep(4);
        }
        return null;
    }

    // Waits until the report returns to baseline (button released).
    static void WaitForBaseline(IntPtr handle, byte[] buf,
        byte[] baseline, int stableCount, int timeoutMs)
    {
        int consecutive = 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!ReadFile(handle, buf, (uint)buf.Length, out uint n, IntPtr.Zero) || n == 0)
            { Thread.Sleep(10); continue; }
            var copy = buf[..(int)n];
            int cmp = Math.Min(baseline.Length, copy.Length);
            bool match = true;
            for (int i = 0; i < cmp; i++) if (copy[i] != baseline[i]) { match = false; break; }
            if (match) { if (++consecutive >= stableCount) return; }
            else consecutive = 0;
            Thread.Sleep(4);
        }
    }

    static int CountBits(byte b)
    { int c = 0; while (b != 0) { c += b & 1; b >>= 1; } return c; }

    static bool IsPressed(byte[] report, PS2InputEntry entry)
    {
        if (!entry.IsValid || entry.Byte >= report.Length) return false;
        return (report[entry.Byte] & entry.Mask) == entry.Value;
    }

    // ── Calibration wizard ─────────────────────────────────────────────────────
    static readonly (string id, string label)[] CALIB_STEPS =
    {
        ("green",     "GREEN fret (lowest)"),
        ("red",       "RED fret"),
        ("yellow",    "YELLOW fret"),
        ("blue",      "BLUE fret"),
        ("orange",    "ORANGE fret (highest)"),
        ("strumUp",   "Strum UP"),
        ("strumDown", "Strum DOWN"),
        ("starPower", "STAR POWER / tilt / select"),
    };

    static PS2Calibration? RunCalibration(IntPtr handle, string deviceName)
    {
        const int STABLE      = 5;       // consecutive identical reads required
        const int STEP_MS     = 30_000;  // 30 s per step before giving up
        const int BASELINE_MS = 5_000;

        Console.WriteLine($"[PS2] Starting calibration for: {deviceName}");
        BroadcastCalibrationState(true, "baseline", "Hold still — reading baseline", deviceName);

        var buf  = new byte[64];
        var cal  = new PS2Calibration { Name = deviceName };
        var entries = new PS2InputEntry[CALIB_STEPS.Length];

        // Establish baseline (no buttons pressed)
        var baseline = ReadStableReport(handle, buf, STABLE, BASELINE_MS);
        if (baseline == null)
        {
            Console.WriteLine("[PS2] Calibration aborted: could not read baseline");
            BroadcastCalibrationState(false);
            return null;
        }

        for (int s = 0; s < CALIB_STEPS.Length; s++)
        {
            var (stepId, stepLabel) = CALIB_STEPS[s];
            Console.WriteLine($"[PS2] Press: {stepLabel}");
            BroadcastCalibrationState(true, stepId, stepLabel, deviceName);

            var pressed = WaitForDiff(handle, buf, baseline, STABLE, STEP_MS);
            if (pressed == null)
            {
                Console.WriteLine($"[PS2] Timed out on '{stepLabel}' — aborting");
                BroadcastCalibrationState(false);
                return null;
            }

            // Find the byte with the most bits changed from baseline
            int diffByte = -1; byte diffMask = 0, diffValue = 0;
            for (int i = 1; i < Math.Min(baseline.Length, pressed.Length); i++)
            {
                byte d = (byte)(pressed[i] ^ baseline[i]);
                if (d != 0 && (diffByte < 0 || CountBits(d) > CountBits(diffMask)))
                { diffByte = i; diffMask = d; diffValue = (byte)(pressed[i] & d); }
            }

            if (diffByte < 0)
            {
                Console.WriteLine($"[PS2] No change detected for '{stepLabel}' — skipping");
                entries[s] = new PS2InputEntry(); // stays IsValid=false
            }
            else
            {
                entries[s] = new PS2InputEntry { Byte = diffByte, Mask = diffMask, Value = diffValue };
                Console.WriteLine($"[PS2]   byte[{diffByte}] mask=0x{diffMask:X2} value=0x{diffValue:X2}");
            }

            WaitForBaseline(handle, buf, baseline, STABLE, 5_000);
        }

        cal.Green     = entries[0];
        cal.Red       = entries[1];
        cal.Yellow    = entries[2];
        cal.Blue      = entries[3];
        cal.Orange    = entries[4];
        cal.StrumUp   = entries[5];
        cal.StrumDown = entries[6];
        cal.StarPower = entries[7];

        Console.WriteLine("[PS2] Calibration complete.");
        BroadcastCalibrationState(false);
        return cal;
    }

    // ── Normal poll loop using a saved calibration ─────────────────────────────
    static void PollWithCalibration(IntPtr handle, PS2Calibration cal, int player)
    {
        var buf = new byte[64];
        while (true)
        {
            if (!ReadFile(handle, buf, (uint)buf.Length, out uint n, IntPtr.Zero) || n == 0)
                break;

            bool g  = IsPressed(buf, cal.Green);
            bool r  = IsPressed(buf, cal.Red);
            bool y  = IsPressed(buf, cal.Yellow);
            bool b  = IsPressed(buf, cal.Blue);
            bool o  = IsPressed(buf, cal.Orange);
            bool su = IsPressed(buf, cal.StrumUp);
            bool sd = IsPressed(buf, cal.StrumDown);

            UpdatePlayerState(player, new GuitarState
            {
                Connected      = true,
                GuitarName     = cal.Name,
                InstrumentType = "guitar",
                G = g, R = r, Y = y, B = b, O = o,
                Strum = su ? "up" : sd ? "down" : "neutral",
            });
        }
    }

    // ── Main PS2 poll task ─────────────────────────────────────────────────────
    static void PollPS2()
    {
        Console.WriteLine("[PS2] PS2 adapter watcher started (UNTESTED)");
        var calibrations = LoadCalibrations();

        while (true)
        {
            // ── Scan HID for a PS2 adapter ───────────────────────────────────
            IntPtr handle = INVALID_HANDLE_VALUE;
            string deviceName = "", vidPid = "";
            int    player     = 0;

            try
            {
                HidD_GetHidGuid(out Guid hidGuid);
                var devInfoSet = SetupDiGetClassDevs(ref hidGuid, null,
                    IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                if (devInfoSet == INVALID_HANDLE_VALUE)
                { Thread.Sleep(2000); continue; }

                uint idx = 0;
                var devIfaceData = new SP_DEVICE_INTERFACE_DATA();
                devIfaceData.cbSize = (uint)Marshal.SizeOf(devIfaceData);

                while (SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero,
                    ref hidGuid, idx++, ref devIfaceData))
                {
                    SetupDiGetDeviceInterfaceDetail(devInfoSet, ref devIfaceData,
                        IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;

                    var detailBuf = Marshal.AllocHGlobal((int)reqSize);
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    SetupDiGetDeviceInterfaceDetail(devInfoSet, ref devIfaceData,
                        detailBuf, reqSize, out _, IntPtr.Zero);
                    string path = Marshal.PtrToStringAuto(IntPtr.Add(detailBuf, 4)) ?? "";
                    Marshal.FreeHGlobal(detailBuf);

                    if (string.IsNullOrEmpty(path) || path.ToUpper().Contains("IG_")) continue;

                    var tmp = CreateFile(path, GENERIC_READ,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (tmp == INVALID_HANDLE_VALUE) continue;

                    var attrs = new HIDD_ATTRIBUTES();
                    attrs.Size = (uint)Marshal.SizeOf(attrs);
                    if (!HidD_GetAttributes(tmp, ref attrs))
                    { CloseHandle(tmp); continue; }

                    // Skip anything already in KnownGuitars
                    if (KnownGuitars.Any(g => g.vid == attrs.VendorID && g.pid == attrs.ProductID))
                    { CloseHandle(tmp); continue; }

                    // Get product string for name-based matching
                    var nameBuf = new byte[256];
                    string pname = HidD_GetProductString(tmp, nameBuf, (uint)nameBuf.Length)
                        ? Encoding.Unicode.GetString(nameBuf).TrimEnd('\0')
                        : "";

                    if (!IsPS2Adapter(pname, attrs.VendorID))
                    { CloseHandle(tmp); continue; }

                    // Found one
                    handle     = tmp;
                    deviceName = string.IsNullOrWhiteSpace(pname)
                        ? $"Unknown PS2 Adapter ({attrs.VendorID:X4}:{attrs.ProductID:X4})"
                        : pname;
                    vidPid = $"{attrs.VendorID:X4}:{attrs.ProductID:X4}";
                    Console.WriteLine($"[PS2] Detected: {deviceName} ({vidPid})");
                    break;
                }

                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PS2] Scan error: {ex.Message}");
                Thread.Sleep(2000); continue;
            }

            if (handle == INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            { Thread.Sleep(2000); continue; }

            // ── Calibrate if needed ──────────────────────────────────────────
            if (!calibrations.TryGetValue(vidPid, out var cal))
            {
                cal = RunCalibration(handle, deviceName);
                if (cal == null) { CloseHandle(handle); Thread.Sleep(2000); continue; }
                calibrations[vidPid] = cal;
                SaveCalibrations(calibrations);
            }

            // ── Poll ─────────────────────────────────────────────────────────
            player = FindFreePlayerSlot();
            Console.WriteLine($"[PS2] Polling {deviceName} as player {player}");
            UpdatePlayerState(player, new GuitarState
                { Connected = true, GuitarName = deviceName, InstrumentType = "guitar" });

            PollWithCalibration(handle, cal, player);

            // Device disconnected
            Console.WriteLine($"[PS2] {deviceName} disconnected");
            UpdatePlayerState(player, new GuitarState { Connected = false });
            CloseHandle(handle);
            Thread.Sleep(2000);
        }
    }

    // =========================================================================
    // GHWTDE STAR POWER READER — AOB scan edition
    // Dynamically locates the star-power byte each game session by scanning
    // the heap for a known byte signature rather than a fixed address (which
    // changes between runs due to GHWTDE's custom pool allocator).
    //
    // The write instruction  mov [esi+08],eax  at 0x0061AFF4 (fixed — no ASLR)
    // writes the SP value into a "property descriptor" struct pointed to by ESI.
    // ESI is always a return value from the property-lookup function 0x004D6AA0,
    // so no static pointer chain to ESI exists and a heap AOB scan is required.
    //
    // Pattern: the 4-byte property-ID hash at struct+4..+7.
    //   struct+0..+1  runtime type tag  — skipped (varies per session)
    //   struct+2      0x01              — integer type discriminator (verified post-match)
    //   struct+3      metadata byte     — skipped (may vary)
    //   struct+4..+7  33 3D BE 92       — compile-time CRC hash, uniquely identifies StarPower
    //   struct+8      SP value 0/1      — verified post-match
    // Post-match validation: struct+2==0x01 AND struct+8∈{0,1}.
    //
    // If scan still fails: in CE "what writes to address" → mov [esi+08],eax →
    // view ESI in the memory hex panel (NOT disassembler), check the 4 bytes at
    // ESI+4 match 33 3D BE 92; if not, update SP_AOB to those bytes.
    // =========================================================================
    const uint PROCESS_VM_READ       = 0x0010;
    const uint PROCESS_QUERY_LIMITED = 0x1000;

    // 4-byte AOB — the property-ID hash embedded in the SP property-descriptor struct.
    // Full header layout:  ??  ??  01  ??  [33 3D BE 92]  [SP byte]
    //   [+0..+1]  runtime type tag        — variable per session, skipped
    //   [+2]      type discriminator 0x01 — stable (integer), verified post-match
    //   [+3]      metadata byte           — may vary, skipped
    //   [+4..+7]  property-ID hash 0x92BE3D33 — compile-time CRC, uniquely identifies SP
    //   [+8]      SP value (0 = off, 1 = active) — verified post-match
    // We scan for the 4-byte hash and validate struct+2==0x01 and struct+8∈{0,1}.
    static readonly byte[] SP_AOB        = { 0x33, 0x3D, 0xBE, 0x92 };
    const           int    SP_AOB_OFFSET = 4;   // SP byte is 4 bytes after hash start = struct+8

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint   AllocationProtect;
        public uint   __pad0;           // natural-alignment pad (Win10: low word = PartitionId)
        public IntPtr RegionSize;       // SIZE_T — 8 bytes on x64
        public uint   State;
        public uint   Protect;
        public uint   Type;
        public uint   __pad1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualQueryEx(
        IntPtr hProcess, IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    // CloseHandle already declared in the HID section above.

    // Walks every readable committed non-image region of the 32-bit GHWTDE process
    // searching for SP_AOB; returns the address SP_AOB_OFFSET bytes past the first
    // match, or IntPtr.Zero if nothing was found.
    static IntPtr AobScan(IntPtr hProcess)
    {
        const uint MEM_COMMIT = 0x1000;
        const uint MEM_IMAGE  = 0x1000000; // skip PE-image sections (.text/.rdata/etc.)
        const uint PAGE_GUARD = 0x100;     // skip guard pages — touching them raises exception
        const uint PAGE_NOACC = 0x01;      // PAGE_NOACCESS

        byte[] pat   = SP_AOB;
        int    spOff = SP_AOB_OFFSET;
        uint   mbiSz = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        long   addr  = 0x00010000L;
        long   ceil  = 0x80000000L;

        int regionsScanned = 0;
        long bytesScanned  = 0;

        while (addr < ceil)
        {
            if (VirtualQueryEx(hProcess, new IntPtr(addr), out var mbi, mbiSz) == IntPtr.Zero)
                break;

            long regionBase = mbi.BaseAddress.ToInt64();
            long regionSize = mbi.RegionSize.ToInt64();

            if (regionSize <= 0)
            {
                addr = Math.Max(addr + 0x1000L, regionBase + 0x1000L);
                continue;
            }

            // Protect field: lower byte = base protection, upper bits = modifiers.
            uint baseProt = mbi.Protect & 0xFF;

            bool committed  = mbi.State == MEM_COMMIT;
            bool notImage   = mbi.Type  != MEM_IMAGE;
            bool notGuard   = (mbi.Protect & PAGE_GUARD) == 0;
            bool readable   = baseProt != PAGE_NOACC && baseProt != 0x10 /* PAGE_EXECUTE */;
            bool bigEnough  = regionSize >= pat.Length + spOff;

            if (committed && notImage && notGuard && readable && bigEnough)
            {
                const int CHUNK = 16 * 1024 * 1024;
                long regionEnd = regionBase + regionSize;
                long readAt    = regionBase;

                while (readAt < regionEnd)
                {
                    int  toRead = (int)Math.Min(CHUNK, regionEnd - readAt);
                    var  buf    = new byte[toRead];
                    bool rdOk   = ReadProcessMemory(hProcess, new IntPtr(readAt),
                                      buf, toRead, out int nRead);

                    if (rdOk && nRead > pat.Length + spOff)
                    {
                        regionsScanned++;
                        bytesScanned += nRead;

                        int limit = nRead - spOff - 1;
                        for (int i = 0; i <= limit && i + pat.Length <= nRead; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < pat.Length && match; j++)
                                match = buf[i + j] == pat[j];

                            if (match)
                            {
                                // Validate 1: SP byte (struct+8, 4 bytes past hash) must be 0 or 1.
                                byte spVal = buf[i + spOff];
                                if (spVal > 1) continue;

                                // Validate 2: type discriminator at struct+2 (= hash_start - 2)
                                // must be 0x01 (integer).  This byte is literally checked by the
                                // dispatcher before branching to the write instruction.
                                if (i < 2 || buf[i - 2] != 0x01) continue;

                                long spPtr = readAt + i + spOff;
                                Console.WriteLine(
                                    $"[GHWTDE] AOB hit at 0x{readAt + i:X8} → SP @ 0x{spPtr:X8} " +
                                    $"(scanned {regionsScanned} regions, {bytesScanned / 1024 / 1024} MB)");
                                return new IntPtr(spPtr);
                            }
                        }
                    }

                    readAt += nRead > 0 ? nRead : CHUNK;
                }
            }

            addr = regionBase + Math.Max(regionSize, 0x1000L);
        }

        Console.WriteLine($"[GHWTDE] Scan complete: {regionsScanned} regions, " +
            $"{bytesScanned / 1024 / 1024} MB searched — pattern not found");

        return IntPtr.Zero;
    }

    static void PollGHWTDE()
    {
        const string PROC_NAME = "GHWT_Definitive";
        const int    POLL_MS   = 16;    // ~60 Hz once SP address is known
        const int    SCAN_MS   = 2000;  // retry when process absent or scan fails

        Console.WriteLine("[GHWTDE] Star power watcher started — looking for GHWT_Definitive.exe");

        IntPtr hProc      = IntPtr.Zero;
        IntPtr spAddr     = IntPtr.Zero;
        int    lastPid    = -1;
        bool   lastSP     = false;
        int    verifyTick = 0;   // counts 16 ms polls; resets on signature check

        while (true)
        {
            // ── Find / re-attach ──────────────────────────────────────────────
            var procs = Process.GetProcessesByName(PROC_NAME);
            if (procs.Length == 0)
            {
                if (hProc != IntPtr.Zero)
                {
                    CloseHandle(hProc);
                    hProc   = IntPtr.Zero;
                    spAddr  = IntPtr.Zero;
                    lastPid = -1;
                    Console.WriteLine("[GHWTDE] Process gone — waiting...");
                    SetStarPowerAll(false);
                    lastSP = false;
                }
                Thread.Sleep(SCAN_MS);
                continue;
            }

            var proc = procs[0];
            foreach (var p in procs) if (p != proc) p.Dispose();

            if (proc.Id != lastPid)
            {
                if (hProc != IntPtr.Zero) CloseHandle(hProc);
                hProc   = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED, false, proc.Id);
                lastPid = proc.Id;
                spAddr  = IntPtr.Zero; // force re-scan on every new attach
                if (hProc == IntPtr.Zero)
                {
                    Console.WriteLine($"[GHWTDE] OpenProcess failed " +
                        $"(err={Marshal.GetLastWin32Error()}) — retrying");
                    proc.Dispose();
                    Thread.Sleep(SCAN_MS);
                    continue;
                }
                Console.WriteLine($"[GHWTDE] Attached PID {proc.Id} — scanning heap for SP signature...");
            }
            proc.Dispose();

            // ── Locate SP address (once per session, cached until process restarts) ─
            if (spAddr == IntPtr.Zero)
            {
                spAddr = AobScan(hProc);

                if (spAddr == IntPtr.Zero)
                {
                    Console.WriteLine($"[GHWTDE] SP not found yet — retrying in {SCAN_MS / 1000}s");
                    Thread.Sleep(SCAN_MS);
                    continue;
                }
            }

            // ── Poll the star-power byte ──────────────────────────────────────
            var  spBuf = new byte[1];
            bool ok    = ReadProcessMemory(hProc, spAddr, spBuf, 1, out int nRead);

            if (!ok || nRead != 1)
            {
                Console.WriteLine("[GHWTDE] Read failed — process may have exited");
                CloseHandle(hProc);
                hProc  = IntPtr.Zero;
                spAddr = IntPtr.Zero;
                SetStarPowerAll(false);
                lastSP = false;
                Thread.Sleep(SCAN_MS);
                continue;
            }

            // SP is always 0 or 1.  Any other value means the heap was
            // reallocated under us (song restart, level change, etc.).
            if (spBuf[0] > 1)
            {
                Console.WriteLine($"[GHWTDE] SP address stale (val=0x{spBuf[0]:X2}) — rescanning...");
                spAddr     = IntPtr.Zero;
                verifyTick = 0;
                SetStarPowerAll(false);
                lastSP = false;
                Thread.Sleep(SCAN_MS);
                continue;
            }

            // Every ~2 s re-confirm the 8-byte signature still precedes the SP byte.
            // Catches the case where a stale address accidentally still reads 0 or 1.
            verifyTick++;
            if (verifyTick >= 125)   // 125 × 16 ms ≈ 2 s
            {
                verifyTick = 0;
                var  sigBuf = new byte[SP_AOB.Length];
                long sigBase = spAddr.ToInt64() - SP_AOB_OFFSET;
                bool sigOk  = ReadProcessMemory(hProc, new IntPtr(sigBase),
                                  sigBuf, SP_AOB.Length, out int sr) &&
                              sr == SP_AOB.Length &&
                              sigBuf.SequenceEqual(SP_AOB);
                if (!sigOk)
                {
                    Console.WriteLine("[GHWTDE] SP signature gone — rescanning...");
                    spAddr     = IntPtr.Zero;
                    verifyTick = 0;
                    SetStarPowerAll(false);
                    lastSP = false;
                    continue;   // rescan immediately, no extra sleep
                }
            }

            bool spNow = spBuf[0] != 0;
            if (spNow != lastSP)
            {
                lastSP = spNow;
                Console.WriteLine(
                    $"[GHWTDE] Star power → {(spNow ? "ACTIVE" : "off")}  (raw=0x{spBuf[0]:X2})");
                SetStarPowerAll(spNow);
            }

            Thread.Sleep(POLL_MS);
        }
    }

    // Broadcasts updated star power state for every connected player slot.
    static void SetStarPowerAll(bool active)
    {
        for (int s = 0; s < 4; s++)
        {
            lock (StateLocks[s])
            {
                if (!PlayerStates[s].Connected) continue;
                if (PlayerStates[s].StarPower == active) continue;
                PlayerStates[s].StarPower = active;
                BroadcastPlayerState(s, PlayerStates[s]);
            }
        }
    }

    // Updates star power for a single player slot (used by PollYARG).
    static void SetStarPowerForPlayer(int player, bool active)
    {
        if (player < 0 || player >= 4) return;
        lock (StateLocks[player])
        {
            if (!PlayerStates[player].Connected) return;
            if (PlayerStates[player].StarPower == active) return;
            PlayerStates[player].StarPower = active;
            BroadcastPlayerState(player, PlayerStates[player]);
        }
    }

    // =========================================================================
    // YARG STAR POWER BRIDGE — UDP listener
    // Receives datagrams from the CHEAPOverlay.YargBridge BepInEx plugin
    // (plugins/YargBridge/) running inside YARG.  The plugin hooks
    // BasePlayer.OnStarPowerStatus and sends:
    //   {"player":<0-3>,"sp":<true|false>}
    // over UDP to localhost:2829.  No polling or memory scanning needed —
    // YARG is open-source with Mono build so BepInEx Harmony patching works.
    // =========================================================================
    static async Task PollYARG()
    {
        const int PORT = 2829;
        Console.WriteLine($"[YARG] Bridge listener started on UDP {PORT}");

        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, PORT));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YARG] Failed to bind UDP {PORT}: {ex.Message} — YARG bridge disabled");
            return;
        }

        using (udp)
        while (true)
        {
            try
            {
                var result = await udp.ReceiveAsync();
                string json = Encoding.UTF8.GetString(result.Buffer);
                using var doc = JsonDocument.Parse(json);
                var root   = doc.RootElement;
                int player = root.GetProperty("player").GetInt32();
                bool sp    = root.GetProperty("sp").GetBoolean();
                SetStarPowerForPlayer(player, sp);
                Console.WriteLine($"[YARG] Player {player} star power → {(sp ? "ACTIVE" : "off")}");
            }
            catch (ObjectDisposedException) { break; }
            catch { /* malformed packet — ignore and keep listening */ }
        }
    }

    // =========================================================================
    // RB3DX STAR POWER READER — RPCS3 (PS3 emulator)
    // Rock Band 3 Deluxe runs inside RPCS3. The PS3 guest memory is a large
    // MEM_PRIVATE allocation above 4 GB in the RPCS3 host process VA space.
    //
    // PPC/Cell is big-endian.  OverdriveMeter::mState (offset +0x18c from
    // object base, 4-byte big-endian enum) holds:
    //   0 = initial  1 = filling  2 = ready  3 = deploying (SP active)
    // We read only the LSB (offset +0x18f) and compare to 3.
    //
    // ── Structural scan — no hardcoded bytes needed ───────────────────────────
    // OverdriveMeter (HMX engine, same source as Wii decomp DarkRTA/rb3) has
    // 7 consecutive ObjPtr<T,D> fields starting at object+0x190.  Each ObjPtr
    // is 12 bytes: [vtable(4)][mOwner=this(4)][mPtr(4)].
    // Because PS3 has no ASLR, both vtable and mOwner are invariant per-session:
    //   • vtable  — ObjRef vtable; same PS3 EA for every ObjPtr of this type
    //   • mOwner  — self-referential pointer back to the OverdriveMeter object
    // We scan for position i (4-byte aligned) where:
    //   buf[i..i+3]   == buf[i+12..i+15]   (vtable repeats at next ObjPtr)
    //   buf[i+4..i+7] == buf[i+16..i+19]   (mOwner repeats at next ObjPtr)
    //   BEU32(buf,i+4) == (readAt+i−memBase) − 0x190   (self-referential check)
    //   BEU32(buf,i−4) ≤ 3                 (mState LSB sanity)
    // Return value: host VA of mState LSB = readAt + i − 1.
    // =========================================================================

    // Finds the base host address of RPCS3's PS3 guest-memory allocation.
    // RPCS3 commits PS3 pages as MEM_PRIVATE; this returns the AllocationBase
    // with the most committed private bytes above 4 GB (that's the PS3 mapping).
    static long FindRpcs3MemBase(IntPtr hProcess)
    {
        uint mbiSz = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        const uint MEM_COMMIT  = 0x1000;
        const uint MEM_PRIVATE = 0x20000;
        const long SCAN_FROM   = 0x100000000L;   // 4 GB — above RPCS3's own heap
        const long SCAN_TO     = 0x800000000L;   // 32 GB upper bound
        const long MIN_COMMIT  = 0x02000000L;    // ≥ 32 MB to qualify

        var  byAlloc = new Dictionary<long, long>(); // AllocationBase → committed bytes
        long addr    = SCAN_FROM;

        while (addr < SCAN_TO)
        {
            if (VirtualQueryEx(hProcess, new IntPtr(addr), out var mbi, mbiSz) == IntPtr.Zero) break;
            long regionBase = mbi.BaseAddress.ToInt64();
            long regionSize = mbi.RegionSize.ToInt64();
            if (regionSize <= 0) { addr = Math.Max(addr + 0x1000L, regionBase + 0x1000L); continue; }

            if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE)
            {
                long allocBase = mbi.AllocationBase.ToInt64();
                if (allocBase >= SCAN_FROM)
                {
                    byAlloc.TryGetValue(allocBase, out long prev);
                    byAlloc[allocBase] = prev + regionSize;
                }
            }

            addr = regionBase + Math.Max(regionSize, 0x1000L);
        }

        long bestBase = 0, bestCommit = 0;
        foreach (var kv in byAlloc)
            if (kv.Value > bestCommit) { bestBase = kv.Key; bestCommit = kv.Value; }

        return bestCommit >= MIN_COMMIT ? bestBase : 0;
    }

    // Structural scan for OverdriveMeter in RPCS3.
    //
    // Previous approach assumed PS3 XDR was one contiguous host allocation starting
    // at memBase.  Diagnostic output showed RPCS3 actually spreads PS3 memory across
    // multiple separate host allocations (code segment, heap, etc.) — the old window
    // missed the heap entirely.
    //
    // New approach: scan ALL committed regions in the RPCS3 host VA range (4 GB–32 GB)
    // and INFER memBase from the ObjPtr self-referential pointer.
    //   • Found mResetTrig vtable at host VA H, mOwner stores PS3 EA V2
    //   • mResetTrig is at object+0x190, so object PS3 EA = V2
    //   • memBase = H − V2 − 0x190
    // This works regardless of how RPCS3 partitions its host VA space.
    //
    // Returns host VA of mState LSB (object+0x18f) and sets foundMemBase.
    // Returns all OverdriveMeter mState LSB addresses found (deduplicated by PS3 EA).
    // foundMemBase is set from the first match — used only for re-verification.
    static List<IntPtr> AobScanRb3(IntPtr hProcess, out long foundMemBase)
    {
        foundMemBase = 0;
        var results  = new List<IntPtr>();
        var seenEA   = new HashSet<uint>(); // deduplicate by mOwner (PS3 EA of object)
        uint  mbiSz = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        const uint MEM_COMMIT  = 0x1000;
        const uint PAGE_GUARD  = 0x100;
        const uint PAGE_NOACC  = 0x01;
        const int  CHUNK       = 16 * 1024 * 1024;
        const long SCAN_FROM   = 0x100000000L;  // 4 GB
        const long SCAN_TO     = 0x800000000L;  // 32 GB

        long addr       = SCAN_FROM;
        int  regions    = 0;
        long bytes      = 0;
        int  diagMstate = 0, diagObjPtr = 0, diagOwner = 0, diagAlign = 0;

        while (addr < SCAN_TO)
        {
            if (VirtualQueryEx(hProcess, new IntPtr(addr), out var mbi, mbiSz) == IntPtr.Zero) break;
            long regionBase = mbi.BaseAddress.ToInt64();
            long regionSize = mbi.RegionSize.ToInt64();
            if (regionSize <= 0) { addr = Math.Max(addr + 0x1000L, regionBase + 0x1000L); continue; }

            uint baseProt  = mbi.Protect & 0xFF;
            bool scannable = mbi.State == MEM_COMMIT
                && (mbi.Protect & PAGE_GUARD) == 0
                && baseProt != PAGE_NOACC && baseProt != 0x10
                && regionSize >= 24;

            if (scannable)
            {
                long regionEnd = Math.Min(regionBase + regionSize, SCAN_TO);
                long readAt    = regionBase;
                while (readAt < regionEnd)
                {
                    int toRead = (int)Math.Min(CHUNK, regionEnd - readAt);
                    var buf    = new byte[toRead];
                    if (!ReadProcessMemory(hProcess, new IntPtr(readAt), buf, toRead, out int nRead)
                        || nRead < 24)
                    {
                        readAt += nRead > 0 ? nRead : CHUNK; continue;
                    }
                    regions++; bytes += nRead;

                    // Need buf[i+28..i+31] for the third ObjPtr owner → stop at nRead-32
                    for (int i = 4; i <= nRead - 32; i += 4)
                    {
                        // ── mState pre-filter: 4-byte BE enum, top 3 bytes = 0x00 ──────
                        if (buf[i-4] != 0 || buf[i-3] != 0 || buf[i-2] != 0) continue;
                        if (buf[i-1] > 3) continue;
                        diagMstate++;

                        // ── vtable + mOwner must repeat at ObjPtr #2 (+12) and #3 (+24) ─
                        // Checking three consecutive ObjPtrs makes false positives vanishingly rare.
                        if (buf[i]   != buf[i+12] || buf[i+1] != buf[i+13] ||
                            buf[i+2] != buf[i+14] || buf[i+3] != buf[i+15] ||
                            buf[i]   != buf[i+24] || buf[i+1] != buf[i+25] ||
                            buf[i+2] != buf[i+26] || buf[i+3] != buf[i+27]) continue;

                        if (buf[i+4] != buf[i+16] || buf[i+5] != buf[i+17] ||
                            buf[i+6] != buf[i+18] || buf[i+7] != buf[i+19] ||
                            buf[i+4] != buf[i+28] || buf[i+5] != buf[i+29] ||
                            buf[i+6] != buf[i+30] || buf[i+7] != buf[i+31]) continue;
                        diagObjPtr++;

                        // ── mOwner must be a plausible PS3 heap EA, and 4-byte aligned ──
                        uint v2 = BEU32(buf, i + 4);
                        if (v2 < 0x00100000u || v2 > 0x0FFF0000u) continue;
                        if ((v2 & 3) != 0) continue;
                        diagOwner++;

                        // ── Vtable must also be a plausible PS3 code address, 4-byte aligned
                        uint v1 = BEU32(buf, i);
                        if (v1 < 0x00010000u || v1 > 0x0FFF0000u) continue;
                        if ((v1 & 3) != 0) continue;
                        // Reject repeating-value regions (vtable == mOwner is a red flag)
                        if (v1 == v2) continue;

                        // ── Infer memBase from the self-referential pointer ───────────────
                        // host VA of position i  = readAt + i  (= PS3 EA of mResetTrig vtable)
                        // PS3 EA of mResetTrig   = v2 + 0x190
                        // so memBase             = (readAt + i) − v2 − 0x190
                        long candidateBase = (readAt + i) - (long)v2 - 0x190L;
                        if (candidateBase < SCAN_FROM || candidateBase >= SCAN_TO) continue;
                        diagAlign++; // survived alignment gate (kept for diag counter continuity)

                        if (seenEA.Add(v2))  // only record each PS3 EA once
                        {
                            if (foundMemBase == 0) foundMemBase = candidateBase;
                            long spPtr = readAt + i - 1;
                            results.Add(new IntPtr(spPtr));
                            Console.WriteLine(
                                $"[RB3DX] OverdriveMeter candidate: PS3 EA 0x{v2:X8} " +
                                $"mState hostVA 0x{spPtr:X} ({results.Count} total)");
                        }
                    }

                    readAt += nRead > 0 ? nRead : CHUNK;
                }
            }

            addr = regionBase + Math.Max(regionSize, 0x1000L);
        }

        Console.WriteLine($"[RB3DX] Scan complete: {regions} regions, {bytes / 1024 / 1024} MB — {results.Count} candidate(s)");
        Console.WriteLine($"[RB3DX] Diag: mState={diagMstate} ObjPtr={diagObjPtr} owner={diagOwner} survived={diagAlign}");
        return results;
    }

    // Read a big-endian uint32 from a byte array.
    static uint BEU32(byte[] b, int i) =>
        ((uint)b[i] << 24) | ((uint)b[i+1] << 16) | ((uint)b[i+2] << 8) | b[i+3];

    // One-shot diagnostic: lists every committed region within the 256 MB PS3 window
    // and searches for the "OverdriveMeterDir" class-name string.  Runs once per
    // RPCS3 session (immediately after memBase is found) and prints to console.
    static void DiagnoseRpcs3Memory(IntPtr hProcess, long memBase)
    {
        uint  mbiSz       = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        const uint  MEM_COMMIT = 0x1000;
        const uint  PAGE_GUARD = 0x100;
        const uint  PAGE_NOACC = 0x01;
        const long  PS3_SZ     = 0x10000000L;  // 256 MB
        long ceiling = memBase + PS3_SZ;

        // UTF-8 / ASCII bytes for "OverdriveMeterDir"
        byte[] needle = System.Text.Encoding.ASCII.GetBytes("OverdriveMeterDir");

        Console.WriteLine("[RB3DX diag] Committed regions in PS3 window:");
        int  regionCount = 0;
        long totalBytes  = 0;
        bool stringFound = false;

        long addr = memBase;
        while (addr < ceiling)
        {
            if (VirtualQueryEx(hProcess, new IntPtr(addr), out var mbi, mbiSz) == IntPtr.Zero) break;
            long rBase = mbi.BaseAddress.ToInt64();
            long rSize = mbi.RegionSize.ToInt64();
            if (rSize <= 0) { addr = Math.Max(addr + 0x1000, rBase + 0x1000); continue; }

            uint prot = mbi.Protect & 0xFF;
            if (mbi.State == MEM_COMMIT
                && (mbi.Protect & PAGE_GUARD) == 0
                && prot != PAGE_NOACC && prot != 0x10)
            {
                long ps3Start = rBase - memBase;
                long ps3End   = ps3Start + rSize;
                Console.WriteLine(
                    $"[RB3DX diag]   PS3 EA 0x{ps3Start:X8}-0x{ps3End:X8}  " +
                    $"({rSize / 1024} KB, type=0x{mbi.Type:X})");
                regionCount++;
                totalBytes += rSize;

                // Scan for "OverdriveMeterDir"
                if (!stringFound)
                {
                    var buf = new byte[(int)Math.Min(rSize, 16 * 1024 * 1024)];
                    if (ReadProcessMemory(hProcess, new IntPtr(rBase), buf, buf.Length, out int nr) && nr >= needle.Length)
                    {
                        for (int i = 0; i <= nr - needle.Length; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < needle.Length && match; j++)
                                match = buf[i + j] == needle[j];
                            if (match)
                            {
                                long ps3Ea = ps3Start + i;
                                Console.WriteLine(
                                    $"[RB3DX diag]   *** \"OverdriveMeterDir\" found at PS3 EA 0x{ps3Ea:X8} ***");
                                stringFound = true;
                                break;
                            }
                        }
                    }
                }
            }

            addr = rBase + Math.Max(rSize, 0x1000L);
            if (addr >= ceiling) break;
        }

        Console.WriteLine(
            $"[RB3DX diag] Total: {regionCount} regions, {totalBytes / 1024 / 1024} MB committed. " +
            $"Class name string: {(stringFound ? "FOUND" : "NOT FOUND")}");
    }

    // Controller-driven SP discovery — loops until narrowed to 1 candidate.
    //   START  = baseline snapshot (SP not deployed)
    //   SELECT = deploys SP + after snapshot
    // Intersects hits across runs to eliminate false positives.
    static void DiscoverSpFlag(IntPtr hProc, long memBase)
    {
        const ushort BTN_SELECT = 0x0020;
        const ushort BTN_START  = 0x0010;
        const long   PS3_START  = 0x00800000L;
        const long   PS3_END    = 0x01000000L;
        int  len   = (int)(PS3_END - PS3_START);
        long hBase = memBase + PS3_START;

        var votes = new Dictionary<uint, int>(); // PS3 EA → times seen
        int run   = 0;

        while (true)
        {
            run++;
            Console.WriteLine($"[RB3DX] Discovery run {run} — press ENTER with SP off for baseline");
            Console.ReadLine();

            var before = new byte[len];
            if (!ReadProcessMemory(hProc, new IntPtr(hBase), before, len, out int n1) || n1 < 1024)
            { Console.WriteLine("[RB3DX] Discovery: read failed, retrying..."); run--; continue; }

            Console.WriteLine("[RB3DX] Discovery: baseline snapped — press SELECT to deploy SP");
            WaitForButton(BTN_SELECT);
            Thread.Sleep(300); // let button-press bytes clear
            Console.WriteLine("[RB3DX] Discovery: snapshotting mid-deployment...");

            var during = new byte[len];
            if (!ReadProcessMemory(hProc, new IntPtr(hBase), during, len, out int n2) || n2 < 1024)
            { Console.WriteLine("[RB3DX] Discovery: read failed, retrying..."); run--; continue; }

            Console.WriteLine("[RB3DX] Discovery: let SP run out naturally, then press ENTER...");
            Console.ReadLine();
            Console.WriteLine("[RB3DX] Discovery: snapshotting post-SP...");

            var after = new byte[len];
            if (!ReadProcessMemory(hProc, new IntPtr(hBase), after, len, out int n3) || n3 < 1024)
            { Console.WriteLine("[RB3DX] Discovery: read failed, retrying..."); run--; continue; }

            // Valid: 0 in baseline, non-zero mid-deploy, back to 0 post-SP
            var hits = new HashSet<uint>();
            int cmp  = Math.Min(Math.Min(n1, n2), n3);
            for (int i = 0; i < cmp; i++)
                if (before[i] == 0 && during[i] != 0 && after[i] == 0)
                    hits.Add((uint)(PS3_START + i));

            if (hits.Count == 0)
            {
                Console.WriteLine("[RB3DX] Discovery: no clean hits — relaxing post-SP check");
                for (int i = 0; i < cmp; i++)
                    if (before[i] == 0 && during[i] != 0)
                        hits.Add((uint)(PS3_START + i));
            }

            // Vote
            foreach (var ea in hits)
            {
                votes.TryGetValue(ea, out int v);
                votes[ea] = v + 1;
            }

            // Show addresses that appeared in more than half the runs so far
            int threshold = (run + 1) / 2;
            var leading   = votes.Where(kv => kv.Value >= threshold)
                                 .OrderByDescending(kv => kv.Value)
                                 .ToList();

            Console.WriteLine($"[RB3DX] Discovery: run {run} — {leading.Count} address(es) seen in >= {threshold}/{run} runs:");
            foreach (var kv in leading)
                Console.WriteLine($"[RB3DX] Discovery:   PS3 EA 0x{kv.Key:X8}  ({kv.Value}/{run} runs)  hostVA 0x{memBase + kv.Key:X}");

            if (leading.Count == 1 && leading[0].Value == run)
            {
                uint ea = leading[0].Key;
                Console.WriteLine($"[RB3DX] Discovery: FOUND — PS3 EA 0x{ea:X8}  (hit every run)");
                break;
            }
            else
            {
                Console.WriteLine("[RB3DX] Discovery: run again to narrow down");
            }
        }
    }

    static void WaitForButton(ushort button)
    {
        bool wasDown = false;
        while (true)
        {
            bool down = false;
            for (uint i = 0; i < 4; i++)
            {
                var st = new XINPUT_STATE();
                if (XInputGetState(i, ref st) != 0) continue;
                if ((st.Gamepad.wButtons & button) != 0) { down = true; break; }
            }
            if (down && !wasDown) return;
            wasDown = down;
            Thread.Sleep(16);
        }
    }

    static void PollRB3DX()
    {
        // ── RB3DX star power support — work in progress ──────────────────────
        //
        // Approach: structural AOB scan for OverdriveMeter (HMX engine ObjPtr
        // array pattern) to derive PS3 memBase, then poll Player::mDeployingBandEnergy
        // at a hardcoded PS3 EA.  memBase varies per session due to host ASLR;
        // PS3 EAs are deterministic for a given RB3DX version.
        //
        // Current status: memBase scan works (AobScanRb3 finds consistent candidates),
        // but the correct PS3 EA for mDeployingBandEnergy has not been confirmed —
        // discovered candidates fluctuate even when SP is not active.
        // Leaving infrastructure in place for future research.
        //
        // See also: AobScanRb3, DiscoverSpFlag, FindRpcs3MemBase, DiagnoseRpcs3Memory
        // Decomp ref: DarkRTA/rb3 — Player.h::mDeployingBandEnergy, OverdriveMeter.cpp

        Console.WriteLine("[RB3DX] Support not yet implemented — skipping");
    }
}

// =============================================================================
// GUITAR STATE
// =============================================================================
class GuitarState : IEquatable<GuitarState>
{
    public int    Player     { get; set; } = 0;
    public bool   Connected  { get; set; }
    public string GuitarName { get; set; }
    public bool   G          { get; set; }
    public bool   R          { get; set; }
    public bool   Y          { get; set; }
    public bool   B          { get; set; }
    public bool   O          { get; set; }
    public string Strum      { get; set; } = "neutral";
    public bool   MouthOpen  { get; set; }
    // Solo frets (HID RB guitars only) — used by overlay to draw dots
    public bool   SoloG { get; set; }
    public bool   SoloR { get; set; }
    public bool   SoloY { get; set; }
    public bool   SoloB { get; set; }
    public bool   SoloO { get; set; }
    // Drum pads (MIDI) — separate from guitar frets so they don't conflict
    public bool   Kick     { get; set; }
    public bool   DrumR    { get; set; }
    public bool   DrumY    { get; set; }
    public bool   DrumB    { get; set; }
    public bool   DrumG    { get; set; }
    // GH orange cymbal
    public bool   DrumO    { get; set; }
    // RB cymbals (distinct from toms of the same colour)
    public bool   DrumYCym { get; set; }
    public bool   DrumBCym { get; set; }
    public bool   DrumGCym { get; set; }
    // Drum kit mode: "gh" | "rb" | "" (empty = guitar/no drums)
    public string DrumMode        { get; set; } = "";
    // Instrument type: "guitar" | "drums" | "keys"
    public string InstrumentType  { get; set; } = "guitar";
    // Star power — set by PollGHWTDE; true while SP is active/deployed in GHWTDE
    public bool   StarPower       { get; set; }

    // JSON property names match what the overlay expects
    public int    player        => Player;
    public bool   connected     => Connected;
    public string guitarName  => GuitarName;
    public bool   g           => G;
    public bool   r           => R;
    public bool   y           => Y;
    public bool   b           => B;
    public bool   o           => O;
    public string strum       => Strum;
    public bool   mouthOpen   => MouthOpen;
    public bool   kick        => Kick;
    public bool   drumR       => DrumR;
    public bool   drumY       => DrumY;
    public bool   drumB       => DrumB;
    public bool   drumG       => DrumG;
    public bool   drumO       => DrumO;
    public bool   drumYCym    => DrumYCym;
    public bool   drumBCym    => DrumBCym;
    public bool   drumGCym    => DrumGCym;
    public string drumMode       => DrumMode;
    public string instrumentType => InstrumentType;
    public bool   soloG          => SoloG;
    public bool   soloR       => SoloR;
    public bool   soloY       => SoloY;
    public bool   soloB       => SoloB;
    public bool   soloO       => SoloO;
    public bool   starPower   => StarPower;

    public bool Equals(GuitarState other) =>
        other != null &&
        Connected == other.Connected &&
        G == other.G && R == other.R && Y == other.Y &&
        B == other.B && O == other.O && Strum == other.Strum &&
        MouthOpen == other.MouthOpen &&
        Kick == other.Kick && DrumR == other.DrumR && DrumY == other.DrumY &&
        DrumB == other.DrumB && DrumG == other.DrumG &&
        DrumO == other.DrumO && DrumMode == other.DrumMode &&
        DrumYCym == other.DrumYCym && DrumBCym == other.DrumBCym && DrumGCym == other.DrumGCym &&
        SoloG == other.SoloG && SoloR == other.SoloR && SoloY == other.SoloY &&
        SoloB == other.SoloB && SoloO == other.SoloO &&
        InstrumentType == other.InstrumentType &&
        StarPower == other.StarPower;
}
