using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CapsLockRemapper
{
    static class Program
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_CAPITAL = 0x14;
        private const int VK_LWIN = 0x5B;
        private const int VK_SPACE = 0x20;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;

        private const int KEYEVENTF_KEYUP = 0x0002;
        
        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;
        private static bool _capsLockDown = false;
        private static long _capsLockStartTime = 0;
        private static int _holdDurationMs = 1000;
        private static bool _blockNativeToggle = true;

        private static bool _modifierPressedAlone = false;
        private static int _currentModifier = 0;

        private static string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
        private static NotifyIcon _trayIcon;

        // IME guard — keeps Chinese conversion mode sticky when block toggle is on
        private static System.Windows.Forms.Timer _imeGuardTimer = null;
        private const uint IME_CMODE_NATIVE = 0x0001; // Chinese character input mode
        private static long _suppressGuardUntil = 0;  // epoch tick after which guard resumes
        private static int _wrongModeCount = 0;        // debounce: consecutive ticks in English mode

        [STAThread]
        public static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "KboardLanguageLayoutKeybindAppMutex", out createdNew))
            {
                if (!createdNew)
                {
                    return; // App is already running
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                
                LoadConfig();

                _hookID = SetHook(_proc);
                if (_blockNativeToggle) StartImeGuard();

                _trayIcon = new NotifyIcon();
                _trayIcon.Text = "Kboard-language-layout-keybind (Settings)";
                _trayIcon.Icon = GenerateTrayIcon(); // DYNAMIC TRAY ICON
                
                ContextMenu trayMenu = new ContextMenu();
                trayMenu.MenuItems.Add("Settings", OnSettings);
                trayMenu.MenuItems.Add("Exit", OnExit);
                _trayIcon.ContextMenu = trayMenu;
                _trayIcon.Visible = true;
                _trayIcon.DoubleClick += OnSettings;
                
                Application.Run();
                
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                StopImeGuard();
                UnhookWindowsHookEx(_hookID);
            }
        }
        
        private static Icon GenerateTrayIcon() {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (GraphicsPath p = new GraphicsPath()) {
                    p.AddArc(0, 0, 10, 10, 180, 90);
                    p.AddArc(bmp.Width - 11, 0, 10, 10, 270, 90);
                    p.AddArc(bmp.Width - 11, bmp.Height - 11, 10, 10, 0, 90);
                    p.AddArc(0, bmp.Height - 11, 10, 10, 90, 90);
                    p.CloseAllFigures();
                    using (Brush bgBrush = new SolidBrush(Color.FromArgb(106, 90, 205))) {
                        g.FillPath(bgBrush, p);
                    }
                }
                
                using (Font font = new Font("Segoe UI", 16, FontStyle.Bold))
                using (Brush brush = new SolidBrush(Color.White)) {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    // Nudge Y a tiny bit so rendering matches center roughly cleanly
                    g.DrawString("中", font, brush, new RectangleF(0, 2, 32, 32), sf);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private static void LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                string[] lines = File.ReadAllLines(_configPath);
                if (lines.Length > 0) {
                    int loadedVal;
                    if (int.TryParse(lines[0], out loadedVal)) {
                        _holdDurationMs = loadedVal;
                    }
                }
                if (lines.Length > 1) {
                    bool loadedBool;
                    if (bool.TryParse(lines[1], out loadedBool)) {
                        _blockNativeToggle = loadedBool;
                    }
                }
            }
            else
            {
                SaveConfig(1000, true);
            }
        }

        public static void SaveConfig(int duration, bool blockToggle)
        {
            _holdDurationMs = duration;
            _blockNativeToggle = blockToggle;
            if (_blockNativeToggle) StartImeGuard();
            else StopImeGuard();
            try {
                File.WriteAllLines(_configPath, new string[] { duration.ToString(), blockToggle.ToString() });
            } catch { /* ignore write errors */ }
        }

        private static void StartImeGuard()
        {
            if (_imeGuardTimer != null) return;
            _imeGuardTimer = new System.Windows.Forms.Timer();
            _imeGuardTimer.Interval = 400;
            _imeGuardTimer.Tick += ImeGuardTick;
            _imeGuardTimer.Start();
        }

        private static void StopImeGuard()
        {
            if (_imeGuardTimer == null) return;
            _imeGuardTimer.Stop();
            _imeGuardTimer.Dispose();
            _imeGuardTimer = null;
        }

        // Runs every 400ms: if the foreground window's IME silently dropped out of
        // native (Chinese) mode — e.g. due to a tray-click focus round-trip — put it back.
        // Suppressed briefly after a language switch, and debounced to 2 consecutive ticks
        // before correcting, to prevent tray indicator flicker.
        private static void ImeGuardTick(object sender, EventArgs e)
        {
            if (Environment.TickCount < _suppressGuardUntil) return;

            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { _wrongModeCount = 0; return; }
            IntPtr himc = ImmGetContext(hwnd);
            if (himc == IntPtr.Zero) { _wrongModeCount = 0; return; }
            uint conv, sent;
            if (ImmGetConversionStatus(himc, out conv, out sent) && (conv & IME_CMODE_NATIVE) == 0)
            {
                _wrongModeCount++;
                if (_wrongModeCount >= 2)
                {
                    ImmSetConversionStatus(himc, conv | IME_CMODE_NATIVE, sent);
                    _wrongModeCount = 0;
                }
            }
            else
            {
                _wrongModeCount = 0;
            }
            ImmReleaseContext(hwnd, himc);
        }

        private static void OnSettings(object sender, EventArgs e)
        {
            SettingsForm settings = new SettingsForm(_holdDurationMs, _blockNativeToggle);
            if (settings.ShowDialog() == DialogResult.OK)
            {
                SaveConfig(settings.Duration, settings.BlockNativeToggle);
            }
        }

        private static void OnExit(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                KBDLLHOOKSTRUCT kbdStruct = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));

                bool isInjected = (kbdStruct.flags & 0x10) != 0;

                if (!isInjected)
                {
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        if (kbdStruct.vkCode == VK_CAPITAL)
                        {
                            _modifierPressedAlone = false; 
                            if (!_capsLockDown)
                            {
                                _capsLockDown = true;
                                _capsLockStartTime = Environment.TickCount;
                            }
                            return (IntPtr)1;
                        }
                        else if (kbdStruct.vkCode == VK_LSHIFT || kbdStruct.vkCode == VK_RSHIFT || kbdStruct.vkCode == VK_SHIFT ||
                                 kbdStruct.vkCode == VK_LCONTROL || kbdStruct.vkCode == VK_RCONTROL || kbdStruct.vkCode == VK_CONTROL)
                        {
                            if (!_modifierPressedAlone) {
                                _modifierPressedAlone = true;
                                _currentModifier = kbdStruct.vkCode;
                            }
                        }
                        else
                        {
                            _modifierPressedAlone = false;
                        }
                    }
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        if (kbdStruct.vkCode == VK_CAPITAL)
                        {
                            if (_capsLockDown)
                            {
                                _capsLockDown = false;
                                long duration = Environment.TickCount - _capsLockStartTime;

                                if (duration > _holdDurationMs)
                                {
                                    keybd_event((byte)VK_CAPITAL, 0x3A, 0, UIntPtr.Zero);
                                    keybd_event((byte)VK_CAPITAL, 0x3A, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                }
                                else
                                {
                                    // Suppress the IME guard for 1.5s so the language-switch
                                    // animation doesn't race with the guard and cause tray flicker.
                                    _suppressGuardUntil = Environment.TickCount + 1500;
                                    _wrongModeCount = 0;
                                    keybd_event((byte)VK_LWIN, 0, 0, UIntPtr.Zero);
                                    keybd_event((byte)VK_SPACE, 0, 0, UIntPtr.Zero);
                                    keybd_event((byte)VK_SPACE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                    keybd_event((byte)VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                }
                            }
                            return (IntPtr)1;
                        }

                        if (_blockNativeToggle && _modifierPressedAlone && _currentModifier == kbdStruct.vkCode)
                        {
                            if (kbdStruct.vkCode == VK_LSHIFT || kbdStruct.vkCode == VK_RSHIFT || kbdStruct.vkCode == VK_SHIFT ||
                                kbdStruct.vkCode == VK_LCONTROL || kbdStruct.vkCode == VK_RCONTROL || kbdStruct.vkCode == VK_CONTROL)
                            {
                                keybd_event(0x87, 0, 0, UIntPtr.Zero);
                                keybd_event(0x87, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                keybd_event((byte)kbdStruct.vkCode, (byte)kbdStruct.scanCode, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                _modifierPressedAlone = false;
                                return (IntPtr)1;
                            }
                        }
                        _modifierPressedAlone = false;
                    }
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("imm32.dll")]
        private static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint lpfdwConversion, out uint lpfdwSentence);

        [DllImport("imm32.dll")]
        private static extern bool ImmSetConversionStatus(IntPtr hIMC, uint fdwConversion, uint fdwSentence);
    }
    
    // Custom button for high-fidelity rendering
    public class PremiumButton : Button
    {
        private bool _isHovered = false;
        
        public PremiumButton()
        {
            this.FlatStyle = FlatStyle.Flat;
            this.FlatAppearance.BorderSize = 0;
            this.Cursor = Cursors.Hand;
            this.MouseEnter += delegate { _isHovered = true; this.Invalidate(); };
            this.MouseLeave += delegate { _isHovered = false; this.Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(this.Parent.BackColor);

            int radius = 10;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
                path.AddArc(this.Width - (radius * 2), 0, radius * 2, radius * 2, 270, 90);
                path.AddArc(this.Width - (radius * 2), this.Height - (radius * 2), radius * 2, radius * 2, 0, 90);
                path.AddArc(0, this.Height - (radius * 2), radius * 2, radius * 2, 90, 90);
                path.CloseFigure();
                
                Color bgColor = _isHovered ? Color.FromArgb(92, 75, 175) : Color.FromArgb(72, 61, 139);
                using (SolidBrush brush = new SolidBrush(bgColor))
                {
                    g.FillPath(brush, path);
                }
            }

            TextRenderer.DrawText(g, this.Text, this.Font, this.ClientRectangle, this.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    public class SettingsForm : Form
    {
        public int Duration { get; private set; }
        public bool BlockNativeToggle { get; private set; }

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        [DllImportAttribute("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImportAttribute("user32.dll")]
        public static extern bool ReleaseCapture();

        public SettingsForm(int currentDuration, bool currentBlockToggle)
        {
            this.Duration = currentDuration;
            this.BlockNativeToggle = currentBlockToggle;
            
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Size = new Size(380, 310);
            this.BackColor = Color.FromArgb(106, 90, 205); // Violet
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 10);
            this.MouseDown += Form_MouseDown;

            // Form Rounded Corners
            int radius = 15;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
                path.AddArc(this.Width - (radius * 2), 0, radius * 2, radius * 2, 270, 90);
                path.AddArc(this.Width - (radius * 2), this.Height - (radius * 2), radius * 2, radius * 2, 0, 90);
                path.AddArc(0, this.Height - (radius * 2), radius * 2, radius * 2, 90, 90);
                path.CloseFigure();
                this.Region = new Region(path);
            }

            // Close Button
            Label closeLbl = new Label();
            closeLbl.Text = "×"; // Using proper multiplication sign for cleaner look
            closeLbl.Font = new Font("Segoe UI", 18, FontStyle.Bold);
            closeLbl.AutoSize = true;
            closeLbl.Location = new Point(this.Width - 35, 8);
            closeLbl.Cursor = Cursors.Hand;
            closeLbl.ForeColor = Color.FromArgb(230,230,250);
            closeLbl.Click += (s, e) => this.DialogResult = DialogResult.Cancel;
            closeLbl.MouseEnter += (s,e) => closeLbl.ForeColor = Color.White;
            closeLbl.MouseLeave += (s,e) => closeLbl.ForeColor = Color.FromArgb(230,230,250);
            this.Controls.Add(closeLbl);

            // Title
            Label titleLbl = new Label();
            titleLbl.Text = "中 / ENG";
            titleLbl.Font = new Font("Segoe UI", 28, FontStyle.Bold);
            titleLbl.AutoSize = true;
            titleLbl.Location = new Point(this.Width / 2 - 80, 30);
            titleLbl.MouseDown += Form_MouseDown;
            this.Controls.Add(titleLbl);

            Label subTitleLbl = new Label();
            subTitleLbl.Text = "Kboard-language-layout-keybind";
            subTitleLbl.Font = new Font("Segoe UI", 10, FontStyle.Italic);
            subTitleLbl.AutoSize = true;
            subTitleLbl.ForeColor = Color.FromArgb(220, 220, 240);
            subTitleLbl.Location = new Point(this.Width / 2 - 100, 85);
            subTitleLbl.MouseDown += Form_MouseDown;
            this.Controls.Add(subTitleLbl);

            // Settings Container styling
            // Using labels and custom textboxes for cleaner alignment
            Label durLbl = new Label();
            durLbl.Text = "Caps Lock hold duration (ms):";
            durLbl.AutoSize = true;
            durLbl.Location = new Point(45, 140);
            this.Controls.Add(durLbl);

            TextBox durTxt = new TextBox();
            durTxt.Text = currentDuration.ToString();
            durTxt.Location = new Point(255, 137);
            durTxt.Size = new Size(60, 25);
            durTxt.BackColor = Color.White;
            durTxt.ForeColor = Color.Black;
            durTxt.BorderStyle = BorderStyle.FixedSingle;
            durTxt.TextAlign = HorizontalAlignment.Center;
            this.Controls.Add(durTxt);

            CheckBox blockChk = new CheckBox();
            blockChk.Text = "Block default Shift/Ctrl layout toggle rules";
            blockChk.Checked = currentBlockToggle;
            blockChk.AutoSize = true;
            blockChk.Location = new Point(45, 185);
            this.Controls.Add(blockChk);

            // Save Button
            PremiumButton saveBtn = new PremiumButton();
            saveBtn.Text = "Save settings";
            saveBtn.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            saveBtn.ForeColor = Color.White;
            saveBtn.Size = new Size(160, 45);
            saveBtn.Location = new Point(this.Width / 2 - 80, 240);
            saveBtn.Click += (s, e) => {
                int newDur;
                if (int.TryParse(durTxt.Text, out newDur)) {
                    this.Duration = newDur;
                    this.BlockNativeToggle = blockChk.Checked;
                    this.DialogResult = DialogResult.OK;
                } else {
                    MessageBox.Show("Please enter a valid number for milliseconds.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            this.Controls.Add(saveBtn);
            
            // VERSION TRACKING LABEL
            Label verLbl = new Label();
            verLbl.Text = "Version 3.0";
            verLbl.Font = new Font("Segoe UI", 8);
            verLbl.AutoSize = true;
            verLbl.ForeColor = Color.FromArgb(180, 180, 220);
            verLbl.Location = new Point(10, this.Height - 25);
            this.Controls.Add(verLbl);
        }

        private void Form_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }
    }
}
