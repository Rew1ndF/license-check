using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinHK3;
using static System.Resources.ResXFileRef;
using static WinHK3.Form1;

namespace WinHK3
{
    // ── Тип слота: приоритет распределения столов ──────────────────────
    public enum SlotType
    {
        Default = 0,   // обычные столы
        Active = 1,   // приоритетные (фиш за столом / активная игра)
        Inactive = 2    // неактивные / ожидание
    }

    public class TableSlot
    {
        public Rectangle Rect;
        public int Order;
        public SlotType Type = SlotType.Default;
        /// <summary>Индекс внутри своего типа (1-based). Используется для сортировки без прыжков.</summary>
        public int TypeIndex = 0;
    }

    // DTO для сериализации DynamicAction → JS (устойчиво к обфускации ConfuserEx)
    internal sealed class ActionDto
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("betSize")] public string BetSize { get; set; }
        [JsonProperty("useSize")] public bool UseSize { get; set; }
        [JsonProperty("isSet")] public bool IsSet { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        [JsonProperty("isBase")] public bool IsBase { get; set; }
        [JsonProperty("hideSize")] public bool HideSize { get; set; }
    }

    // DTO для сериализации слотов лейаута → JS (устойчиво к обфускации ConfuserEx)
    internal sealed class SlotDto
    {
        [JsonProperty("x")] public int X { get; set; }
        [JsonProperty("y")] public int Y { get; set; }
        [JsonProperty("w")] public int W { get; set; }
        [JsonProperty("h")] public int H { get; set; }
        [JsonProperty("order")] public int Order { get; set; }
        [JsonProperty("slotType")] public int SlotType { get; set; }
        [JsonProperty("typeIndex")] public int TypeIndex { get; set; }
    }

    // DTO монитора для превью лейаута
    internal sealed class MonitorDto
    {
        [JsonProperty("x")] public int X { get; set; }
        [JsonProperty("y")] public int Y { get; set; }
        [JsonProperty("w")] public int W { get; set; }
        [JsonProperty("h")] public int H { get; set; }
        [JsonProperty("primary")] public bool Primary { get; set; }
        [JsonProperty("taskbarH")] public int TaskbarH { get; set; } // высота панели задач (WorkingArea vs Bounds)
    }

    // DTO-обёртка для превью лейаута
    internal sealed class LayoutPreviewDto
    {
        [JsonProperty("screenW")] public int ScreenW { get; set; }
        [JsonProperty("screenH")] public int ScreenH { get; set; }
        [JsonProperty("slots")] public List<SlotDto> Slots { get; set; }
        [JsonProperty("monitors")] public List<MonitorDto> Monitors { get; set; }
    }

    public class LayoutConfig
    {
        public string Name = "Default";
        public List<TableSlot> Slots = new List<TableSlot>();
    }

    // ═══════════════════════════════════════════════════════
    //  OVERLAY — поверх окна 1win (остаётся WinForms)
    // ═══════════════════════════════════════════════════════
    public class OverlayForm : Form
    {
        public bool IsActive = false;
        public bool ShowBorder = true;           // управляет видимостью обводки
        public Color BorderColor = Color.FromArgb(99, 102, 241); // цвет обводки
        public List<DebugPoint> DebugPoints = new List<DebugPoint>();
        public DebugPoint InputPoint = null;

        // Список хоткеев для отображения в правом нижнем углу (debug mode)
        public List<HotkeyEntry> HotkeyEntries = new List<HotkeyEntry>();
        // Показывать список биндов поверх стола (независимо от debug режима)
        public bool ShowBinds = false;

        public class HotkeyEntry
        {
            public string Name;
            public string Key;
            public bool Enabled;
        }

        // Callback когда пользователь перетащил точку ПКМ
        public Action<DebugPoint, float, float> OnPointMoved;

        public class DebugPoint
        {
            public float RelX, RelY;
            public string Label;
            public Color Color;
            public bool IsInput;
            public int ActionId = -1;
        }

        private bool _debugMode = false;
        private DebugPoint _dragging = null;
        private bool _rmb = false;
        public bool IsDragging { get { return _rmb; } }

        public bool DebugMode
        {
            get { return _debugMode; }
            set { _debugMode = value; UpdateClickThrough(); }
        }

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.Black;
            TransparencyKey = Color.Black;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
        }

        // Делаем overlay кликабельным в debug режиме
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000; // WS_EX_LAYERED
                if (!_debugMode) cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT только когда не debug
                return cp;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        private void UpdateClickThrough()
        {
            if (!IsHandleCreated) return;
            const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            if (_debugMode) ex &= ~WS_EX_TRANSPARENT;
            else ex |= WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, ex);
        }

        private DebugPoint HitTest(Point p)
        {
            foreach (var pt in DebugPoints)
            {
                int px = (int)(Width * pt.RelX), py = (int)(Height * pt.RelY);
                if (Math.Abs(px - p.X) <= 16 && Math.Abs(py - p.Y) <= 16) return pt;
            }
            if (InputPoint != null)
            {
                int px = (int)(Width * InputPoint.RelX), py = (int)(Height * InputPoint.RelY);
                if (Math.Abs(px - p.X) <= 16 && Math.Abs(py - p.Y) <= 16) return InputPoint;
            }
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!DebugMode) return;
            if (e.Button == MouseButtons.Right)
            {
                _dragging = HitTest(e.Location);
                _rmb = _dragging != null;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_rmb && _dragging != null)
            {
                _dragging.RelX = Math.Max(0f, Math.Min(1f, (float)e.X / Width));
                _dragging.RelY = Math.Max(0f, Math.Min(1f, (float)e.Y / Height));
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_rmb && _dragging != null)
            {
                OnPointMoved?.Invoke(_dragging, _dragging.RelX, _dragging.RelY);
                _dragging = null; _rmb = false;
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!IsActive) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            if (ShowBorder)
            {
                int t = (int)(DateTime.Now.Millisecond / 100) % 10;
                int alpha = Math.Min(160 + t * 9, 255);
                using (var pen = new Pen(Color.FromArgb(alpha, BorderColor), 3))
                    g.DrawRectangle(pen, 2, 2, Width - 5, Height - 5);

                int cs = 16;
                using (var pen2 = new Pen(BorderColor, 2))
                {
                    g.DrawLine(pen2, 0, 0, cs, 0); g.DrawLine(pen2, 0, 0, 0, cs);
                    g.DrawLine(pen2, Width - cs, 0, Width, 0); g.DrawLine(pen2, Width, 0, Width, cs);
                    g.DrawLine(pen2, 0, Height - cs, 0, Height); g.DrawLine(pen2, 0, Height, cs, Height);
                    g.DrawLine(pen2, Width - cs, Height, Width, Height); g.DrawLine(pen2, Width, Height - cs, Width, Height);
                }
            }

            // ── Список биндов в правом нижнем углу стола — масштабируется с окном ──
            if (ShowBinds && HotkeyEntries.Count > 0)
            {
                // Масштаб шрифта относительно высоты окна (стола)
                float scale = Math.Max(0.6f, Math.Min(2.0f, Height / 750f));
                float fontSize = 8.5f * scale;
                float fontSizeSmall = 7.5f * scale;
                int rowH = (int)(22 * scale);
                int padBottom = (int)(14 * scale);
                int padRight = (int)(14 * scale);
                int panelW = (int)(240 * scale);

                var font = new Font("Segoe UI", fontSize, FontStyle.Bold);
                var fontSmall = new Font("Segoe UI", fontSizeSmall);

                int panelH = HotkeyEntries.Count * rowH + (int)(4 * scale);
                // Правый нижний угол стола
                int px = Width - panelW - padRight;
                int py = Height - panelH - padBottom;

                int iy = py;
                foreach (var h in HotkeyEntries)
                {
                    Color nameColor = h.Enabled ? Color.FromArgb(220, 220, 235) : Color.FromArgb(90, 90, 100);
                    Color keyColor = h.Enabled ? Color.FromArgb(0, 229, 255) : Color.FromArgb(70, 70, 80);

                    var keyStr = h.Key ?? "—";
                    var keySz = g.MeasureString(keyStr, font);
                    int kw = (int)keySz.Width + (int)(4 * scale);

                    using (var kBg = new SolidBrush(Color.FromArgb(h.Enabled ? 40 : 15, 0, 229, 255)))
                        g.FillRectangle(kBg, px, iy, kw, rowH - (int)(4 * scale));

                    DrawShadowString(g, keyStr, font, keyColor, px + (int)(3 * scale), iy + (int)(2 * scale));
                    DrawShadowString(g, h.Name, fontSmall, nameColor, px + kw + (int)(6 * scale), iy + (int)(4 * scale));

                    iy += rowH;
                }
                font.Dispose(); fontSmall.Dispose();
            }

            if (!DebugMode) return;

            // Заголовок debug mode — только текст, без фона
            DrawShadowString(g, "● DEBUG MODE  [ПКМ = переместить]",
                new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Color.FromArgb(251, 191, 36), 6, 4);

            foreach (var pt in DebugPoints) DrawDebugMarker(g, pt);
            if (InputPoint != null) DrawDebugMarker(g, InputPoint);
        }

        // Рисует текст с тёмной тенью — хорошо читается на любом фоне без панели
        private static void DrawShadowString(Graphics g, string text, Font font, Color color, int x, int y)
        {
            using (var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
            {
                g.DrawString(text, font, shadow, x + 1, y + 1);
                g.DrawString(text, font, shadow, x - 1, y + 1);
                g.DrawString(text, font, shadow, x + 1, y - 1);
            }
            using (var br = new SolidBrush(color))
                g.DrawString(text, font, br, x, y);
        }

        private void DrawDebugMarker(Graphics g, DebugPoint pt)
        {
            int px = (int)(Width * pt.RelX), py = (int)(Height * pt.RelY);
            int r = pt.IsInput ? 14 : 12;
            using (var br = new SolidBrush(Color.FromArgb(60, pt.Color))) g.FillEllipse(br, px - r, py - r, r * 2, r * 2);
            using (var pen = new Pen(pt.Color, 2)) g.DrawEllipse(pen, px - r, py - r, r * 2, r * 2);
            using (var pen2 = new Pen(pt.Color, 1.5f)) { g.DrawLine(pen2, px - r + 2, py, px + r - 2, py); g.DrawLine(pen2, px, py - r + 2, px, py + r - 2); }
            using (var br2 = new SolidBrush(pt.Color)) g.FillEllipse(br2, px - 3, py - 3, 6, 6);

            string info = $"{pt.Label}  ({(int)(pt.RelX * 100)}%, {(int)(pt.RelY * 100)}%)";
            var labelFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            int lx = px + r + 4, ly = py - 8;
            var sz = g.MeasureString(info, labelFont);
            if (lx + sz.Width > Width - 4) lx = px - (int)sz.Width - r - 4;
            // Текст с тенью — без фонового прямоугольника
            DrawShadowString(g, info, labelFont, pt.Color, lx, ly);
            labelFont.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════
    //  РЕДАКТОР СЕТКИ
    // ═══════════════════════════════════════════════════════
    public class LayoutEditorForm : Form
    {
        private readonly List<TableSlot> _slots = new List<TableSlot>();
        private TableSlot _sel = null;
        private bool _resizing = false;
        private Point _lastMouse;
        public LayoutConfig ResultConfig { get; private set; }

        private readonly Point _origin; // = VirtualScreen.Location

        private readonly int WIN_TITLE_H = 30;
        private readonly float TABLE_AR = 960f / 750f;

        private const int MIN_W = 512;
        private const int MIN_H = 200;

        private readonly Label _measureLabel;

        public LayoutEditorForm(LayoutConfig existing, int measuredTitleH = 0, float measuredAR = 0f)
        {
            Text = "LAYOUT EDITOR";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            BackColor = Color.FromArgb(10, 10, 16);
            Opacity = 0.93;
            DoubleBuffered = true;
            TopMost = true;

            _origin = SystemInformation.VirtualScreen.Location;

            if (measuredTitleH > 0 && measuredTitleH < 100) WIN_TITLE_H = measuredTitleH;
            if (measuredAR > 0.5f && measuredAR < 5f) TABLE_AR = measuredAR;

            if (existing?.Slots.Count > 0)
                foreach (var s in existing.Slots)
                    _slots.Add(new TableSlot { Rect = s.Rect, Order = s.Order, Type = s.Type, TypeIndex = s.TypeIndex });

            var bar = new Panel
            {
                Size = new Size(700, 52),
                Location = new Point((Screen.PrimaryScreen.Bounds.Width - 700) / 2, 20),
                BackColor = Color.FromArgb(22, 22, 36)
            };
            bar.Paint += (s, e) => {
                using (var p = new Pen(Color.FromArgb(60, 60, 90)))
                    e.Graphics.DrawRectangle(p, 0, 0, bar.Width - 1, bar.Height - 1);
            };

            var txtName = new TextBox
            {
                Text = existing?.Name ?? "New Layout",
                Size = new Size(130, 28),
                Location = new Point(10, 12),
                Font = new Font("Segoe UI", 9.5f),
                BackColor = Color.FromArgb(14, 14, 22),
                ForeColor = Color.FromArgb(240, 240, 255)
            };

            var btnAdd1 = MkBtn("+ SLOT", 160, Color.FromArgb(99, 102, 241));
            var btnAuto1 = MkBtn("⊞ АВТО 1", 255, Color.FromArgb(20, 184, 166));
            var btnSave = MkBtn("✔ SAVE", 350, Color.FromArgb(34, 197, 94));
            var btnExit = MkBtn("✖ EXIT", 445, Color.FromArgb(239, 68, 68));

            var hint = new Label
            {
                Text = "ПКМ = Удалить  │  Drag = Переместить  │  △ (угол) = Resize",
                ForeColor = Color.FromArgb(110, 110, 150),
                Location = new Point(450, 4),
                AutoSize = true,
                Font = new Font("Segoe UI", 7.5f)
            };

            bool measured = measuredTitleH > 0 && measuredAR > 0;
            _measureLabel = new Label
            {
                Text = measured
                    ? $"✔ Измерено у окна 1win: titlebar={WIN_TITLE_H}px  AR={TABLE_AR:F3}"
                    : $"⚠ Окно 1win не найдено, используются defaults: titlebar={WIN_TITLE_H}px  AR={TABLE_AR:F3}",
                ForeColor = measured ? Color.FromArgb(34, 197, 94) : Color.FromArgb(251, 191, 36),
                Location = new Point(450, 22),
                AutoSize = true,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold)
            };

            // ── Определить на каком экране центр слота ──
            Screen getRectScreen(Rectangle rect)
            {
                int cx = rect.X + rect.Width / 2;
                int cy = rect.Y + rect.Height / 2;
                foreach (var sc2 in Screen.AllScreens)
                    if (sc2.Bounds.Contains(cx, cy)) return sc2;
                return Screen.PrimaryScreen;
            }

            // ── +SLOT: слот появляется в центре указанного экрана ──
            System.Action makeAddSlot(Screen targetScreen) => () => {
                var sc = targetScreen.WorkingArea;
                int sw = 960;
                int clientH0 = (int)Math.Round(sw / TABLE_AR);
                int sh = clientH0 + WIN_TITLE_H;
                int sx = sc.X + (sc.Width - sw) / 2;
                int sy = sc.Y + (sc.Height - sh) / 2;
                _slots.Add(new TableSlot { Rect = new Rectangle(sx, sy, sw, sh), Order = _slots.Count + 1 });
                Invalidate();
            };

            // ── АВТО: расставляет только слоты, чей центр на targetScreen ──
            void doAutoLayout(Screen targetScreen)
            {
                var candidates = _slots.Where(sl => getRectScreen(sl.Rect) == targetScreen).ToList();
                if (candidates.Count == 0) return;
                var bounds = targetScreen.WorkingArea;
                int n = candidates.Count;
                int cols = (int)Math.Ceiling(Math.Sqrt(n));
                int rows = (int)Math.Ceiling((double)n / cols);
                int slotW = Math.Max(MIN_W, bounds.Width / cols);
                int clientH = (int)Math.Round(slotW / TABLE_AR);
                int slotH = clientH + WIN_TITLE_H;
                if (slotH * rows > bounds.Height)
                {
                    slotH = bounds.Height / rows;
                    clientH = slotH - WIN_TITLE_H;
                    slotW = Math.Max(MIN_W, (int)Math.Round(clientH * TABLE_AR));
                }
                for (int idx = 0; idx < candidates.Count; idx++)
                {
                    int col = idx % cols, row = idx / cols;
                    candidates[idx].Rect = new Rectangle(
                        bounds.X + col * slotW,
                        bounds.Y + row * slotH,
                        slotW, slotH);
                }
                Invalidate();
            }

            btnAdd1.Click += (s, e) => makeAddSlot(Screen.PrimaryScreen)();
            btnAuto1.Click += (s, e) => doAutoLayout(Screen.PrimaryScreen);

            btnSave.Click += (s, e) => {
                ResultConfig = new LayoutConfig { Name = txtName.Text.Trim(), Slots = _slots };
                DialogResult = DialogResult.OK;
            };
            btnExit.Click += (s, e) => Close();

            bar.Controls.AddRange(new Control[] { txtName, btnAdd1, btnAuto1, btnSave, btnExit, hint, _measureLabel });
            Controls.Add(bar);

            // ── Второй тулбар на мониторе 2 (если есть) ──
            Screen screen2 = null;
            foreach (var sc2x in Screen.AllScreens)
                if (!sc2x.Primary) { screen2 = sc2x; break; }

            if (screen2 != null)
            {
                var bar2 = new Panel
                {
                    Size = new Size(200, 52),
                    Location = new Point(screen2.Bounds.X + (screen2.Bounds.Width - 200) / 2, screen2.Bounds.Y + 20),
                    BackColor = Color.FromArgb(22, 22, 36)
                };
                bar2.Paint += (s, e2) => {
                    using (var p2 = new Pen(Color.FromArgb(60, 60, 90)))
                        e2.Graphics.DrawRectangle(p2, 0, 0, bar2.Width - 1, bar2.Height - 1);
                };
                var btnAuto2 = new Button
                {
                    Text = "⊞ АВТО 2",
                    Size = new Size(90, 28),
                    Location = new Point(10, 12),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(20, 150, 140),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnAuto2.FlatAppearance.BorderSize = 0;
                var btnAdd2 = new Button
                {
                    Text = "+ SLOT",
                    Size = new Size(82, 28),
                    Location = new Point(108, 12),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(99, 102, 241),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnAdd2.FlatAppearance.BorderSize = 0;
                var capturedScreen2 = screen2;
                btnAuto2.Click += (s, e) => doAutoLayout(capturedScreen2);
                btnAdd2.Click += (s, e) => makeAddSlot(capturedScreen2)();
                bar2.Controls.AddRange(new Control[] { btnAuto2, btnAdd2 });
                Controls.Add(bar2);
            }

            MouseDown += (s, e) => {
                for (int i = _slots.Count - 1; i >= 0; i--)
                {
                    var sl = _slots[i];
                    var dr = ToClient(sl.Rect);
                    var grip = new Rectangle(dr.Right - 30, dr.Bottom - 30, 30, 30);

                    if (grip.Contains(e.Location))
                    {
                        _sel = sl; _resizing = true; _lastMouse = e.Location;
                        return;
                    }
                    if (dr.Contains(e.Location))
                    {
                        if (e.Button == MouseButtons.Right)
                        {
                            // ── Контекстное меню: тип слота + удаление ──
                            ShowSlotContextMenu(sl, i);
                        }
                        else { _sel = sl; _resizing = false; _lastMouse = e.Location; }
                        return;
                    }
                }
            };

            MouseMove += (s, e) => {
                if (_sel == null) return;
                int dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;

                if (_resizing)
                {
                    var dr = ToClient(_sel.Rect);
                    int nw = Math.Max(MIN_W, e.X - dr.X);
                    int clientW = nw;
                    int clientH = (int)Math.Round(clientW / TABLE_AR);
                    int nh = clientH + WIN_TITLE_H;
                    if (nh < MIN_H) { nh = MIN_H; nw = (int)Math.Round((nh - WIN_TITLE_H) * TABLE_AR); }
                    _sel.Rect.Width = nw;
                    _sel.Rect.Height = nh;
                }
                else
                {
                    _sel.Rect.X += dx;
                    _sel.Rect.Y += dy;

                    const int SNAP = 12;
                    int rx = _sel.Rect.X, ry = _sel.Rect.Y;
                    int rr = rx + _sel.Rect.Width, rb = ry + _sel.Rect.Height;

                    foreach (var sc in Screen.AllScreens)
                    {
                        var wa = sc.WorkingArea;
                        if (Math.Abs(rx - wa.Left) <= SNAP) rx = wa.Left;
                        if (Math.Abs(ry - wa.Top) <= SNAP) ry = wa.Top;
                        if (Math.Abs(rr - wa.Right) <= SNAP) rx = wa.Right - _sel.Rect.Width;
                        if (Math.Abs(rb - wa.Bottom) <= SNAP) ry = wa.Bottom - _sel.Rect.Height;
                    }
                    foreach (var other in _slots)
                    {
                        if (other == _sel) continue;
                        int or = other.Rect.Right, ob = other.Rect.Bottom;
                        if (Math.Abs(rx - or) <= SNAP) rx = or;
                        if (Math.Abs(rr - other.Rect.Left) <= SNAP) rx = other.Rect.Left - _sel.Rect.Width;
                        if (Math.Abs(ry - ob) <= SNAP) ry = ob;
                        if (Math.Abs(rb - other.Rect.Top) <= SNAP) ry = other.Rect.Top - _sel.Rect.Height;
                        if (Math.Abs(rx - other.Rect.Left) <= SNAP) rx = other.Rect.Left;
                        if (Math.Abs(ry - other.Rect.Top) <= SNAP) ry = other.Rect.Top;
                    }
                    _sel.Rect.X = rx;
                    _sel.Rect.Y = ry;
                }
                _lastMouse = e.Location;
                Invalidate();
            };

            MouseUp += (s, e) => { _sel = null; _resizing = false; };
        }

        private Rectangle ToClient(Rectangle r)
            => new Rectangle(r.X - _origin.X, r.Y - _origin.Y, r.Width, r.Height);

        // ── Получить цвет рамки/заливки по типу слота ─────────────────────
        private static Color SlotBorderColor(SlotType t, bool selected)
        {
            if (t == SlotType.Active) return selected ? Color.FromArgb(255, 239, 68, 68) : Color.FromArgb(180, 239, 68, 68);
            if (t == SlotType.Inactive) return selected ? Color.FromArgb(255, 148, 163, 184) : Color.FromArgb(160, 100, 116, 139);
            return selected ? Color.FromArgb(220, 99, 102, 241) : Color.FromArgb(140, 99, 102, 241);
        }
        private static Color SlotFillColor(SlotType t, bool selected)
        {
            if (t == SlotType.Active) return Color.FromArgb(selected ? 70 : 45, 239, 68, 68);
            if (t == SlotType.Inactive) return Color.FromArgb(selected ? 55 : 35, 100, 116, 139);
            return Color.FromArgb(selected ? 75 : 50, 99, 102, 241);
        }
        private static string SlotTypeName(SlotType t)
        {
            if (t == SlotType.Active) return "ACTIVE";
            if (t == SlotType.Inactive) return "INACTIVE";
            return "DEFAULT";
        }

        private void ShowSlotContextMenu(TableSlot sl, int slotIndex)
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(28, 28, 40),
                ForeColor = Color.FromArgb(220, 220, 230),
                Font = new Font("Segoe UI", 9f),
                RenderMode = ToolStripRenderMode.System
            };

            // Радио-пункты типа
            var types = new[] { SlotType.Default, SlotType.Active, SlotType.Inactive };
            var labels = new[] { "⬜  Default (Normal)", "🔴  Active (Priority)", "⚫  Inactive" };
            for (int t = 0; t < types.Length; t++)
            {
                var st = types[t];
                var item = new ToolStripMenuItem(labels[t])
                {
                    Checked = sl.Type == st,
                    CheckOnClick = false,
                    Font = sl.Type == st
                        ? new Font("Segoe UI", 9f, FontStyle.Bold)
                        : new Font("Segoe UI", 9f),
                };
                item.Click += (_, __) =>
                {
                    sl.Type = st;
                    // Пересчитываем TypeIndex
                    var counters = new Dictionary<SlotType, int>
                    { {SlotType.Default,0},{SlotType.Active,0},{SlotType.Inactive,0} };
                    foreach (var s2 in _slots.OrderBy(x => x.Order))
                    { counters[s2.Type]++; s2.TypeIndex = counters[s2.Type]; }
                    Invalidate();
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new ToolStripSeparator());

            var del = new ToolStripMenuItem("🗑  Удалить слот")
            { ForeColor = Color.FromArgb(239, 68, 68) };
            del.Click += (_, __) =>
            {
                _slots.RemoveAt(slotIndex);
                for (int j = 0; j < _slots.Count; j++) _slots[j].Order = j + 1;
                var c2 = new Dictionary<SlotType, int>
                { {SlotType.Default,0},{SlotType.Active,0},{SlotType.Inactive,0} };
                foreach (var s2 in _slots.OrderBy(x => x.Order))
                { c2[s2.Type]++; s2.TypeIndex = c2[s2.Type]; }
                Invalidate();
            };
            menu.Items.Add(del);

            menu.Show(this, PointToClient(Cursor.Position));
        }

        private Button MkBtn(string txt, int x, Color bg)
        {
            var b = new Button
            {
                Text = txt,
                Size = new Size(82, 28),
                Location = new Point(x, 12),
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            foreach (var sc in Screen.AllScreens)
            {
                var r = ToClient(sc.Bounds);
                using (var p = new Pen(Color.FromArgb(35, 255, 255, 255), 1)) g.DrawRectangle(p, r);
                using (var br = new SolidBrush(Color.FromArgb(18, 200, 200, 255))) g.FillRectangle(br, r);
                using (var fnt = new Font("Segoe UI", 13))
                    g.DrawString(sc.DeviceName, fnt, new SolidBrush(Color.FromArgb(22, 200, 200, 255)), r.X + 12, r.Y + 12);

                // ── Рисуем панель задач (taskbar) — область между Bounds и WorkingArea ──
                var wa = sc.WorkingArea;
                var b = sc.Bounds;

                // Каждая сторона где WorkingArea меньше Bounds — там taskbar
                var taskbarRects = new List<Rectangle>();
                if (wa.Top > b.Top)
                    taskbarRects.Add(new Rectangle(b.Left, b.Top, b.Width, wa.Top - b.Top)); // taskbar сверху
                if (wa.Bottom < b.Bottom)
                    taskbarRects.Add(new Rectangle(b.Left, wa.Bottom, b.Width, b.Bottom - wa.Bottom)); // taskbar снизу
                if (wa.Left > b.Left)
                    taskbarRects.Add(new Rectangle(b.Left, b.Top, wa.Left - b.Left, b.Height)); // taskbar слева
                if (wa.Right < b.Right)
                    taskbarRects.Add(new Rectangle(wa.Right, b.Top, b.Right - wa.Right, b.Height)); // taskbar справа

                foreach (var tb in taskbarRects)
                {
                    var tbc = ToClient(tb);
                    using (var tbBr = new SolidBrush(Color.FromArgb(90, 30, 100, 200)))
                        g.FillRectangle(tbBr, tbc);
                    using (var tbPen = new Pen(Color.FromArgb(180, 60, 140, 255), 1.5f))
                        g.DrawRectangle(tbPen, tbc);
                    using (var fnt2 = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                    {
                        int labelH = (int)(tbc.Height / 2f - 6);
                        if (tbc.Height >= 14 && tbc.Width >= 40)
                            g.DrawString("TASKBAR", fnt2, new SolidBrush(Color.FromArgb(160, 100, 180, 255)),
                                tbc.X + 4, tbc.Y + Math.Max(0, labelH));
                    }
                }
            }

            foreach (var sl in _slots)
            {
                var dr = ToClient(sl.Rect);
                bool sel = sl == _sel;

                var titleR = new Rectangle(dr.X, dr.Y, dr.Width, WIN_TITLE_H);
                using (var br = new SolidBrush(Color.FromArgb(sel ? 80 : 55, 60, 60, 80)))
                    g.FillRectangle(br, titleR);
                using (var pen = new Pen(SlotBorderColor(sl.Type, sel), sel ? 1.5f : 1f))
                    g.DrawRectangle(pen, titleR);

                string typeTag = $"[{SlotTypeName(sl.Type)}]";
                string titleTxt = $"  #{sl.Order}·{typeTag}  {sl.Rect.Width}×{sl.Rect.Height}  @({sl.Rect.X},{sl.Rect.Y})";
                using (var fnt = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                    g.DrawString(titleTxt, fnt, new SolidBrush(Color.FromArgb(sel ? 230 : 160, 200, 200, 220)),
                        titleR.X + 4, titleR.Y + (WIN_TITLE_H - 12) / 2);

                var clientR = new Rectangle(dr.X, dr.Y + WIN_TITLE_H, dr.Width, Math.Max(1, dr.Height - WIN_TITLE_H));
                using (var br = new SolidBrush(SlotFillColor(sl.Type, sel)))
                    g.FillRectangle(br, clientR);
                using (var pen = new Pen(SlotBorderColor(sl.Type, sel), sel ? 2.5f : 1.5f))
                    g.DrawRectangle(pen, clientR);

                int dispClientH = sl.Rect.Height - WIN_TITLE_H;
                string clientTxt = $"Client: {sl.Rect.Width} × {dispClientH}  │  AR: {(float)sl.Rect.Width / dispClientH:F2}";
                using (var fnt = new Font("Segoe UI", 8f))
                    g.DrawString(clientTxt, fnt, new SolidBrush(Color.FromArgb(sel ? 180 : 110, 180, 190, 255)),
                        clientR.X + 8, clientR.Y + 8);

                // Легенда типа в центре слота (крупно и полупрозрачно)
                using (var fntBig = new Font("Segoe UI", Math.Max(9f, clientR.Height / 8f), FontStyle.Bold))
                {
                    var typeColor = SlotBorderColor(sl.Type, false);
                    var bigLabel = SlotTypeName(sl.Type);
                    var sz = g.MeasureString(bigLabel, fntBig);
                    g.DrawString(bigLabel, fntBig,
                        new SolidBrush(Color.FromArgb(sel ? 60 : 38, typeColor)),
                        clientR.X + (clientR.Width - sz.Width) / 2,
                        clientR.Y + (clientR.Height - sz.Height) / 2);
                }

                // Подсказка ПКМ
                using (var fntHint = new Font("Segoe UI", 7f))
                    g.DrawString("ПКМ — тип слота", fntHint,
                        new SolidBrush(Color.FromArgb(60, 200, 200, 200)),
                        clientR.X + 8, clientR.Bottom - 18);

                // Уголок для ресайза
                int gs = 28;
                g.FillPolygon(new SolidBrush(Color.FromArgb(sel ? 230 : 150, SlotBorderColor(sl.Type, sel))),
                    new[] {
                        new Point(dr.Right,      dr.Bottom),
                        new Point(dr.Right - gs, dr.Bottom),
                        new Point(dr.Right,      dr.Bottom - gs)
                    });
            }
        }
    }

    // ═══════════════════════════════════════════════════════
    //  TEST ZONE (остаётся WinForms)
    // ═══════════════════════════════════════════════════════
    public partial class Form1 : Form { }

    // ═══════════════════════════════════════════════════════
    //  ВСПОМОГАТЕЛЬНЫЙ КЛАСС: КЛАВИШИ
    // ═══════════════════════════════════════════════════════
    internal static class KeyHelper
    {
        public static readonly Dictionary<string, Keys> JsKeyMap = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
        {
            { "Space",     Keys.Space },     { " ",         Keys.Space },
            { "Enter",     Keys.Enter },     { "Return",    Keys.Return },
            { "Backspace", Keys.Back },
            { "Tab",       Keys.Tab },
            { "Escape",    Keys.Escape },    { "Esc",       Keys.Escape },
            { "Delete",    Keys.Delete },    { "Del",       Keys.Delete },
            { "Insert",    Keys.Insert },
            { "Home",      Keys.Home },      { "End",       Keys.End },
            { "PageUp",    Keys.PageUp },    { "PageDown",  Keys.PageDown },
            { "ArrowUp",   Keys.Up },        { "ArrowDown", Keys.Down },
            { "ArrowLeft", Keys.Left },      { "ArrowRight",Keys.Right },
            { "NumLock",   Keys.NumLock },
            { "CapsLock",  Keys.CapsLock },
            { "PrintScreen",Keys.PrintScreen },
            { "Pause",     Keys.Pause },
            // F-keys
            { "F1",  Keys.F1  }, { "F2",  Keys.F2  }, { "F3",  Keys.F3  }, { "F4",  Keys.F4  },
            { "F5",  Keys.F5  }, { "F6",  Keys.F6  }, { "F7",  Keys.F7  }, { "F8",  Keys.F8  },
            { "F9",  Keys.F9  }, { "F10", Keys.F10 }, { "F11", Keys.F11 }, { "F12", Keys.F12 },
            // Цифры
            { "0", Keys.D0 }, { "1", Keys.D1 }, { "2", Keys.D2 }, { "3", Keys.D3 }, { "4", Keys.D4 },
            { "5", Keys.D5 }, { "6", Keys.D6 }, { "7", Keys.D7 }, { "8", Keys.D8 }, { "9", Keys.D9 },
            { "D0", Keys.D0 }, { "D1", Keys.D1 }, { "D2", Keys.D2 }, { "D3", Keys.D3 }, { "D4", Keys.D4 },
            { "D5", Keys.D5 }, { "D6", Keys.D6 }, { "D7", Keys.D7 }, { "D8", Keys.D8 }, { "D9", Keys.D9 },
            // Shift+цифра (US) → цифровая клавиша
            { "!", Keys.D1 }, { "@", Keys.D2 }, { "#", Keys.D3 }, { "$", Keys.D4 },
            { "%", Keys.D5 }, { "^", Keys.D6 }, { "&", Keys.D7 }, { "*", Keys.D8 },
            { "(", Keys.D9 }, { ")", Keys.D0 },
            // Символьные клавиши
            { "-",  Keys.OemMinus },   { "_",  Keys.OemMinus },
            { "=",  Keys.Oemplus },    { "+",  Keys.Oemplus },
            { "[",  Keys.OemOpenBrackets }, { "{", Keys.OemOpenBrackets },
            { "]",  Keys.OemCloseBrackets }, { "}", Keys.OemCloseBrackets },
            { "\\", Keys.OemBackslash }, { "|", Keys.OemBackslash },
            { ";",  Keys.OemSemicolon }, { ":", Keys.OemSemicolon },
            { "'",  Keys.OemQuotes },  { "\"", Keys.OemQuotes },
            { ",",  Keys.Oemcomma },   { "<",  Keys.Oemcomma },
            { ".",  Keys.OemPeriod },  { ">",  Keys.OemPeriod },
            { "/",  Keys.OemQuestion }, { "?", Keys.OemQuestion },
            { "`",  Keys.Oemtilde },   { "~",  Keys.Oemtilde },
        };

        private static readonly Dictionary<Keys, string> _displayName = new Dictionary<Keys, string>
        {
            { Keys.D0, "0" }, { Keys.D1, "1" }, { Keys.D2, "2" }, { Keys.D3, "3" }, { Keys.D4, "4" },
            { Keys.D5, "5" }, { Keys.D6, "6" }, { Keys.D7, "7" }, { Keys.D8, "8" }, { Keys.D9, "9" },
            { Keys.Space, "Space" }, { Keys.Enter, "Enter" }, { Keys.Back, "Backspace" },
            { Keys.Tab, "Tab" }, { Keys.Escape, "Escape" }, { Keys.Delete, "Delete" },
            { Keys.Insert, "Insert" }, { Keys.Home, "Home" }, { Keys.End, "End" },
            { Keys.PageUp, "PageUp" }, { Keys.PageDown, "PageDown" },
            { Keys.Up, "Up" }, { Keys.Down, "Down" }, { Keys.Left, "Left" }, { Keys.Right, "Right" },
            { Keys.OemMinus, "-" }, { Keys.Oemplus, "=" },
            { Keys.OemOpenBrackets, "[" }, { Keys.OemCloseBrackets, "]" },
            { Keys.OemBackslash, "\\" }, { Keys.OemSemicolon, ";" },
            { Keys.OemQuotes, "'" }, { Keys.Oemcomma, "," },
            { Keys.OemPeriod, "." }, { Keys.OemQuestion, "/" },
            { Keys.Oemtilde, "`" },
        };

        public static string GetKeyDisplayName(Keys key)
        {
            if (_displayName.TryGetValue(key, out string name)) return name;
            return key.ToString();
        }

        public static void ParseKeyCombo(string combo, out Keys key, out bool ctrl, out bool shift)
        {
            key = Keys.None; ctrl = false; shift = false;
            if (string.IsNullOrWhiteSpace(combo) || combo == "—") return;
            var parts = combo.Split(new[] { " + " }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var p = part.Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) { ctrl = true; continue; }
                if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { shift = true; continue; }
                if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (JsKeyMap.TryGetValue(p, out Keys mapped)) { key = mapped; continue; }
                if (Enum.TryParse(p, true, out Keys k)) { key = k; }
            }
        }
    }

    public class TestZoneForm : Form
    {
        private readonly Bitmap _bg;
        private readonly Func<List<Form1.DynamicAction>> _getActions;
        private readonly Func<double> _getGX, _getGY;
        private readonly Func<bool> _getIsGset;
        private const float AR = 960f / 750f;
        private Form1.DynamicAction _activeAction = null;
        private Point _clickPos;
        private bool _showClick = false;
        private readonly System.Windows.Forms.Timer _clickTimer;
        private readonly TextBox _betBox;
        private readonly Label _betLabel;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public TestZoneForm(Bitmap screen, Func<List<Form1.DynamicAction>> getActions, Func<double> getGX, Func<double> getGY, Func<bool> getIsGset)
        {
            Text = "Hold_em NL (ТЕСТОВАЯ ЗОНА)"; ClientSize = new Size(1356, 1041);
            _bg = screen; _getActions = getActions; _getGX = getGX; _getGY = getGY; _getIsGset = getIsGset;
            DoubleBuffered = true; KeyPreview = true; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(10, 10, 16);
            ResizeRedraw = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            _betBox = new TextBox { Font = new Font("Consolas", 13, FontStyle.Bold), TextAlign = HorizontalAlignment.Center, BackColor = Color.FromArgb(14, 14, 22), ForeColor = Color.FromArgb(251, 191, 36), BorderStyle = BorderStyle.None, Visible = false };
            _betLabel = new Label { Text = "СТАВКА", Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), ForeColor = Color.FromArgb(140, 140, 170), BackColor = Color.Transparent, AutoSize = true, Visible = false };
            Controls.Add(_betBox); Controls.Add(_betLabel); _betBox.BringToFront(); _betLabel.BringToFront();

            _betBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Return || e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _betBox.Visible = false; _betLabel.Visible = false; _activeAction = null; _showClick = false; Invalidate(); this.Focus(); } };
            _clickTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _clickTimer.Tick += (s, e) => { _showClick = false; _clickTimer.Stop(); Invalidate(); };
            KeyDown += OnHotkey;
            Resize += (s, e) => { RepositionBetBox(); Invalidate(); };
            RepositionBetBox();
        }

        private void OnHotkey(object sender, KeyEventArgs e)
        {
            if (_betBox.Focused) { _betBox.Visible = false; _betLabel.Visible = false; this.Focus(); }
            var act = _getActions().FirstOrDefault(a => a.Key == e.KeyCode);
            if (act == null) return;
            _activeAction = act; e.Handled = true;
            int w = ClientSize.Width, h = ClientSize.Height;
            bool _isGset = _getIsGset(); double _gx = _getGX(), _gy = _getGY();
            if (act.UseSize && _isGset) { _betBox.Text = act.SizeValue; _betBox.Visible = true; _betLabel.Visible = true; RepositionBetBox(); _clickPos = new Point((int)(w * _gx), (int)(h * _gy)); _showClick = true; _clickTimer.Stop(); _clickTimer.Start(); BeginInvoke(new Action(() => { _betBox.Focus(); _betBox.SelectAll(); })); }
            else if (!act.UseSize && act.IsSet) { _betBox.Visible = false; _betLabel.Visible = false; _clickPos = new Point((int)(w * act.RelX), (int)(h * act.RelY)); _showClick = true; _clickTimer.Stop(); _clickTimer.Start(); }
            Invalidate();
        }

        private void RepositionBetBox()
        {
            bool _isGset = _getIsGset(); double _gx = _getGX(), _gy = _getGY();
            if (!_isGset) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            int bw = Math.Max(90, (int)(w * 0.09)), bh = Math.Max(30, (int)(h * 0.035));
            int cx = (int)(w * _gx), cy = (int)(h * _gy);
            _betBox.Size = new Size(bw, bh); _betBox.Location = new Point(cx - bw / 2, cy - bh / 2);
            _betLabel.Location = new Point(cx - bw / 2, cy - bh / 2 - 18);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        // ── Сохраняем пропорции (960×750) при ресайзе ──────────────
        private const int WM_SIZING = 0x0214;
        private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3;
        private const int WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5;
        private const int WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINRECT { public int Left, Top, Right, Bottom; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SIZING)
            {
                var rc = (WINRECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(WINRECT));
                int edge = m.WParam.ToInt32();

                // Учитываем рамки окна
                var extraW = Width - ClientSize.Width;
                var extraH = Height - ClientSize.Height;

                int clientW = (rc.Right - rc.Left) - extraW;
                int clientH = (rc.Bottom - rc.Top) - extraH;

                // Вычисляем нужный размер клиента по AR (960/750)
                int newClientW, newClientH;

                bool dragLeft = edge == WMSZ_LEFT || edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT;
                bool dragTop = edge == WMSZ_TOP || edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT;
                bool dragRight = edge == WMSZ_RIGHT || edge == WMSZ_TOPRIGHT || edge == WMSZ_BOTTOMRIGHT;
                bool _ = edge == WMSZ_BOTTOM || edge == WMSZ_BOTTOMLEFT || edge == WMSZ_BOTTOMRIGHT; // dragBottom unused

                // Ведущее измерение — то, которое больше изменилось
                if (dragLeft || dragRight)
                {
                    newClientW = clientW;
                    newClientH = (int)(clientW / AR);
                }
                else
                {
                    newClientH = clientH;
                    newClientW = (int)(clientH * AR);
                }

                int totalW = newClientW + extraW;
                int totalH = newClientH + extraH;

                if (dragLeft) rc.Left = rc.Right - totalW;
                else rc.Right = rc.Left + totalW;
                if (dragTop) rc.Top = rc.Bottom - totalH;
                else rc.Bottom = rc.Top + totalH;

                System.Runtime.InteropServices.Marshal.StructureToPtr(rc, m.LParam, true);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int w = ClientSize.Width, h = ClientSize.Height;
            g.Clear(Color.FromArgb(10, 10, 16));
            if (_bg != null)
            {
                // Fill: растягиваем на весь клиент без letterbox — точно как при расстановке столов
                g.DrawImage(_bg, 0, 0, w, h);
            }
            else { g.DrawString("Нет скриншота (t.jpg не найден)", new Font("Segoe UI", 14), new SolidBrush(Color.FromArgb(70, 70, 100)), w / 2 - 130, h / 2 - 12); }
            using (var br = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillRectangle(br, 0, 0, w, h);
            bool _isGset = _getIsGset(); double _gx = _getGX(), _gy = _getGY();
            Point origin = new Point((int)(w * 50.0 / 1358.0), (int)(h * 50.0 / 1040.0));
            DrawMarker(g, origin, Color.FromArgb(34, 197, 94), "START");
            if (_activeAction != null)
            {
                if (_activeAction.UseSize && _isGset) { var pt = new Point((int)(w * _gx), (int)(h * _gy)); DrawDashedLine(g, origin, pt, Color.FromArgb(251, 191, 36)); DrawMarker(g, pt, Color.FromArgb(251, 191, 36), "INPUT"); }
                else if (_activeAction.IsSet) { var pt = new Point((int)(w * _activeAction.RelX), (int)(h * _activeAction.RelY)); DrawDashedLine(g, origin, pt, Color.FromArgb(99, 102, 241)); DrawClickMarker(g, pt); }
            }
            if (_showClick) { int r2 = 22; using (var br2 = new SolidBrush(Color.FromArgb(120, Color.FromArgb(239, 68, 68)))) g.FillEllipse(br2, _clickPos.X - r2, _clickPos.Y - r2, r2 * 2, r2 * 2); using (var pen = new Pen(Color.FromArgb(239, 68, 68), 2)) g.DrawEllipse(pen, _clickPos.X - r2, _clickPos.Y - r2, r2 * 2, r2 * 2); g.FillEllipse(Brushes.White, _clickPos.X - 4, _clickPos.Y - 4, 8, 8); }

            var actions = _getActions();
            if (actions.Count > 0)
            {
                var font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                var fontN = new Font("Segoe UI", 7.5f);
                int rowH = 22, panelW = 240;
                int panelH = actions.Count * rowH + 10;
                int px = w - panelW - 10, py = h - panelH - 10;
                int iy = py;
                foreach (var a in actions)
                {
                    string keyStr = a.Key == Keys.None ? "—" : ((a.UseCtrl ? "Ctrl+" : "") + (a.UseShift ? "Shift+" : "") + KeyHelper.GetKeyDisplayName(a.Key));
                    bool active = a.IsSet || a.UseSize;
                    Color keyCol = active ? Color.FromArgb(0, 229, 255) : Color.FromArgb(70, 70, 80);
                    Color nameCol = active ? Color.FromArgb(220, 220, 235) : Color.FromArgb(90, 90, 100);
                    var keySz = font.GetHeight() > 0 ? e.Graphics.MeasureString(keyStr, font) : new SizeF(30, 16);
                    int kw = (int)keySz.Width + 4;
                    using (var kBg = new SolidBrush(Color.FromArgb(active ? 35 : 12, 0, 229, 255)))
                        e.Graphics.FillRectangle(kBg, px, iy, kw, rowH - 4);
                    using (var sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    {
                        e.Graphics.DrawString(keyStr, font, sh, px + 4, iy + 3);
                        e.Graphics.DrawString(a.DisplayName, fontN, sh, px + kw + 7, iy + 5);
                    }
                    e.Graphics.DrawString(keyStr, font, new SolidBrush(keyCol), px + 3, iy + 2);
                    e.Graphics.DrawString(a.DisplayName, fontN, new SolidBrush(nameCol), px + kw + 6, iy + 4);
                    iy += rowH;
                }
                font.Dispose(); fontN.Dispose();
            }
        }

        void DrawDashedLine(Graphics g, Point a, Point b, Color c) { using (var p = new Pen(c, 2) { DashStyle = DashStyle.Dash }) g.DrawLine(p, a, b); }
        void DrawMarker(Graphics g, Point pt, Color c, string label) { int s = 10; using (var br = new SolidBrush(c)) g.FillRectangle(br, pt.X - s / 2, pt.Y - s / 2, s, s); g.DrawString(label, new Font("Segoe UI", 7.5f, FontStyle.Bold), new SolidBrush(c), pt.X + 8, pt.Y - 8); }
        void DrawClickMarker(Graphics g, Point pt) { int r = 14; using (var br = new SolidBrush(Color.FromArgb(160, Color.FromArgb(239, 68, 68)))) g.FillEllipse(br, pt.X - r, pt.Y - r, r * 2, r * 2); using (var p = new Pen(Color.FromArgb(239, 68, 68), 2)) g.DrawEllipse(p, pt.X - r, pt.Y - r, r * 2, r * 2); using (var p2 = new Pen(Color.White, 2)) { g.DrawLine(p2, pt.X - 6, pt.Y, pt.X + 6, pt.Y); g.DrawLine(p2, pt.X, pt.Y - 6, pt.X, pt.Y + 6); } }
    }

    // ═══════════════════════════════════════════════════════
    //  ГЛАВНАЯ ФОРМА — WebView2 UI + C# логика
    // ═══════════════════════════════════════════════════════
    public partial class Form1 : Form
    {
        public class DynamicAction
        {
            public string DisplayName;
            public double RelX, RelY;
            public bool IsSet;
            public Keys Key = Keys.None;
            public bool UseCtrl, UseShift, UseSize;
            public string SizeValue = "3";
            public int InternalID;
            public bool IsEnabled = true;
            public bool IsBase = false;
            public bool HideSize = false;
            public object BtnSet, BtnKeyBind, TxtSize, ChkCtrl, ChkShift, ChkSize;
        }

        private const string CURRENT_VERSION = "7.3";
        private const string VERSION_URL = "https://raw.githubusercontent.com/Rew1ndF/license-check/refs/heads/main/Version1winHK.json";
        private const string SUPPORT_URL = "https://t.me/Rew1ndF";

        // ── Tracker Version ───────────────────────────────
        public enum TrackerVersion { H2N3, H2N4 }
        private TrackerVersion _trackerVersion = TrackerVersion.H2N4;

        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CHAR = 0x0102;
        private const uint WM_SETTEXT = 0x000C;
        private const uint EM_SETSEL = 0x00B1;
        private const uint MK_LBUTTON = 0x0001;
        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_SETFOCUS = 0x0007;
        // Координаты центра стола (настраиваемые)
        private const double TABLE_CENTER_REL_X = 0.496932515337423;
        private const double TABLE_CENTER_REL_Y = 0.498977505112474;

        private readonly string configsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs");

        // ui.html встроен как Embedded Resource — отдельный файл рядом с exe не нужен
        // (в свойствах ui.html в проекте: Действие при сборке = Внедрённый ресурс)
        private string LoadEmbeddedHtml()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ui.html"));
            if (resourceName == null) return FallbackHtml();
            using (var stream = asm.GetManifestResourceStream(resourceName))
            using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private readonly List<DynamicAction> userActions = new List<DynamicAction>();
        private readonly List<LayoutConfig> layouts = new List<LayoutConfig>();
        private LayoutConfig activeLayout = new LayoutConfig();
        private double globalInputX, globalInputY;
        private bool isGlobalInputSet = false;
        private bool isRunning = false;
        private bool isRussian = true;
        private string partialWindowTitle = "Hold'em NL";
        private readonly List<string> _windowTitleFilters = new List<string>
        {
            "Hold'em NL"
        };

        private bool IsTableWindow(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            if (title.IndexOf("1WIN TOOLS", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (title.IndexOf("1WinPoker - user", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (title.IndexOf("1WinPoker-user", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (title.IndexOf("ТЕСТОВАЯ ЗОНА", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (!string.IsNullOrEmpty(partialWindowTitle) &&
                title.IndexOf(partialWindowTitle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var f in _windowTitleFilters)
                if (!string.IsNullOrEmpty(f) && title.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private const int WIN_TITLE_H_APPROX = 30;
        private int idCounter = 100;
        private readonly string autoSavePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autosave.txt");
        private readonly string layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layouts.txt");
        private readonly string tableSizePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "table_size.txt");
        private readonly string generalPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "general.txt");
        private readonly string baseStatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "basestate.txt");

        private bool snapEnabled = false;
        private bool swapEnabled = false;

        // ── Auto-Start Feature Activation ─────────────────
        private bool _autoStartConverter = false;
        private bool _autoStartLayout = false;
        private string _autoStartLayoutName = "";
        private readonly string _autoStartSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autostart_settings.txt");

        private bool _autoLayoutEnabled = false;
        private readonly System.Windows.Forms.Timer _autoLayoutTimer = new System.Windows.Forms.Timer();
        private readonly Dictionary<IntPtr, int> _hwndSlotMap = new Dictionary<IntPtr, int>();
        private readonly Dictionary<IntPtr, DateTime> _hwndMovedAt = new Dictionary<IntPtr, DateTime>();
        private static readonly TimeSpan AutoLayoutCooldown = TimeSpan.FromMilliseconds(800);
        private readonly Dictionary<IntPtr, RECT> _hwndTargetRect = new Dictionary<IntPtr, RECT>();

        private int _detectStep = 0;
        private int _detectX1, _detectY1;
        private int _tableW = 0, _tableH = 0;

        private readonly OverlayForm overlay = new OverlayForm();
        private ConsoleForm _consoleForm;
        private bool _consoleEnabled = false;

        // ── Screen Capture ────────────────────────────────
        private string _screenSaveFolder = "";
        private System.Threading.Timer _screenTimer;
        private bool _screenRunning = false;
        private int _screenShotCount = 0;
        private long _screenTotalBytes = 0;
        private string _screenFormat = "jpg";
        private int _screenQuality = 85;
        private List<string> _screenTargetTables = new List<string> { "all" };

        // ── Poker Converter ───────────────────────────────
        private readonly PokerConverter _converter = new PokerConverter();
        private System.Windows.Forms.Timer _tableActivityTimer;
        private System.Windows.Forms.Timer _masterScanTimer;
        private readonly string _converterSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "converter_settings_cs.json");

        // ── All Hands archive dir (AppData\WinHK3\AllHands) ──
        private static readonly string AllHandsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinHK3", "AllHands");

        // ── H2N Color Notes (Fish Detection) — вкладка Tables ──────────────
        private readonly H2NColorNoteReader _h2nReader = new H2NColorNoteReader();
        // ── H2N Color Notes (Fish Detection) — вкладка Tables 2 (отдельные настройки) ──
        private readonly H2NColorNoteReader _t2H2nReader = new H2NColorNoteReader();
        // ── H2N Color Notes (Reg Detection) — вкладка Tables 2 ──
        private readonly H2NColorNoteReader _t2RegReader = new H2NColorNoteReader();

        // ── FileSystemWatcher — мгновенное обновление маркеров (.cm) ──────────
        private FileSystemWatcher _t2FishWatcher;
        private FileSystemWatcher _t2RegWatcher;
        private volatile bool _t2CmChangePending = false;
        private System.Windows.Forms.Timer _t2CmDebounceTimer;

        // ── Tables2 SitOut по РАЗДАЧАМ (а не тикам таймера) ──────────────────
        private sealed class T2SitOutHandState
        {
            public string LastHandId = null;
            public int NoFishStreak = 0;
            public bool AlertShown = false;
            public DateTime SnoozedUntil = DateTime.MinValue;
        }
        private readonly Dictionary<string, T2SitOutHandState> _t2SitOutHSt
            = new Dictionary<string, T2SitOutHandState>(StringComparer.OrdinalIgnoreCase);
        private readonly object _t2SitOutHLock = new object();

        // ── Fish Monitor (авто-ситаут при отсутствии фиша) — старый, для TABLES──
        private FishMonitor _fishMonitor;
        private bool _autoSitOutEnabled = false;

        // ── Tables 2: авто-ситаут если нет фиша ──────────────────
        private bool _t2SitOutEnabled = false;  // тумблер
        private int _t2SitOutHands = 3;      // кол-во раздач без фиша до сит-аута
        private bool _t2SitOutAutoMode = false;  // false=уведомление, true=авто
        private int _t2SitOutSnoozeMin = 5;      // "повторить уведомление через X мин"
        // Состояние по столам: tableName → (noFishStreak, lastAlertTime)
        private readonly Dictionary<string, (int streak, DateTime snoozedUntil)> _t2SitOutState
            = new Dictionary<string, (int, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private readonly object _t2SitOutLock = new object();

        /// <summary>Активные алерт-окна по имени стола (UI-поток).</summary>
        private readonly Dictionary<string, FishAlertForm> _fishAlerts
            = new Dictionary<string, FishAlertForm>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Последний снимок таблиц — для AutoLayoutTick чтобы знать fishy/active статус окон.</summary>
        private volatile List<ActiveTableInfo> _lastTableInfos = new List<ActiveTableInfo>();

        // ═══════════════════════════════════════════════════════
        //  TableManager — единый источник истины о состоянии столов
        //  Все компоненты (Layout, Tables, FishMonitor) читают отсюда.
        // ═══════════════════════════════════════════════════════
        private sealed class TableWindowEntry
        {
            public IntPtr Hwnd;
            public string Title;
            public bool Focused;    // GetForegroundWindow() == Hwnd
            public bool Minimized;  // IsIconic
            public Rectangle ClientBounds;
            public DateTime LastSeen;
        }

        /// <summary>Актуальный список окон-столов (обновляется в TableManager.Refresh).</summary>
        private readonly Dictionary<IntPtr, TableWindowEntry> _tableWindows
            = new Dictionary<IntPtr, TableWindowEntry>();
        private readonly object _tableWindowsLock = new object();

        /// <summary>Обновляет снимок окон столов — вызывается из единого таймера.</summary>
        private void RefreshTableWindows()
        {
            var fg = GetForegroundWindow();
            var seen = new List<TableWindowEntry>();

            EnumWindows((hWnd, lp) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                // Пропускаем "cloaked" окна (скрытые на других виртуальных рабочих столах)
                if (DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                string title = sb.ToString();
                if (!IsTableWindow(title)) return true;

                RECT cb = GetClientBounds(hWnd);
                seen.Add(new TableWindowEntry
                {
                    Hwnd = hWnd,
                    Title = title,
                    Focused = (hWnd == fg),
                    Minimized = IsIconic(hWnd),
                    ClientBounds = new Rectangle(cb.Left, cb.Top, cb.Right - cb.Left, cb.Bottom - cb.Top),
                    LastSeen = DateTime.Now
                });
                return true;
            }, IntPtr.Zero);

            lock (_tableWindowsLock)
            {
                // Убираем закрытые окна с grace-периодом 2 секунды
                // (защита от кратковременного пропадания при перерисовке)
                var now2 = DateTime.Now;
                var deadKeys = new List<IntPtr>();
                foreach (var kv in _tableWindows)
                {
                    if (!seen.Any(e => e.Hwnd == kv.Key))
                    {
                        // Окно пропало — ждём 2 сек перед удалением
                        if ((now2 - kv.Value.LastSeen).TotalSeconds > 2.0)
                            deadKeys.Add(kv.Key);
                    }
                }
                foreach (var k in deadKeys) _tableWindows.Remove(k);
                foreach (var e in seen) _tableWindows[e.Hwnd] = e;
            }
        }

        /// <summary>Возвращает снимок всех окон столов (thread-safe копия).</summary>
        private List<TableWindowEntry> GetTableWindowSnapshot()
        {
            lock (_tableWindowsLock)
                return _tableWindows.Values.ToList();
        }

        // ── Кэш HH-файлов для снижения IO ─────────────────────────────
        private sealed class HHFileCache
        {
            public DateTime LastWriteTime = default;
            public ActiveTableInfo Info = null;
        }
        private readonly Dictionary<string, HHFileCache> _hhCache
            = new Dictionary<string, HHFileCache>(StringComparer.OrdinalIgnoreCase);
        private readonly object _hhCacheLock = new object();

        // ── Кэш архивных файлов (путь → (lastHandTime, tableName)) ────
        private sealed class ArchiveFileCache
        {
            public DateTime FileWriteTime = default;
            public DateTime LastHandTime = default;
            public string TableName = null;
        }
        private readonly Dictionary<string, ArchiveFileCache> _archiveCache
            = new Dictionary<string, ArchiveFileCache>(StringComparer.OrdinalIgnoreCase);
        private readonly object _archiveCacheLock = new object();

        // ── AutoSeat ──────────────────────────────────────
        private AutoSeatManager _autoSeat;
        private bool _autoSeatEnabled = false;

        // ── Table Border ──────────────────────────────────
        private bool _tableBorderEnabled = true;
        private Color _tableBorderColor = Color.FromArgb(99, 102, 241); // дефолт — фиолетовый

        // ── Quick Bet (double-click: size point → Raise) ──
        // -1 = выключено, иначе InternalID кнопки у которой активна быстрая ставка
        private readonly HashSet<int> _quickBetActionIds = new HashSet<int>();

        private static readonly string _logFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinHK3", "update_log.txt");

        // ── Лог вкладки TABLES — пишется в папку программы ──────────────────
        private static readonly string _tablesLogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "tables_log.txt");
        private static readonly object _tablesLogLock = new object();
        // _tablesLogTickCount removed (CS0414 – assigned but never read)

        private void TablesLog(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                System.Diagnostics.Debug.WriteLine("[TABLES] " + message);
                lock (_tablesLogLock)
                    File.AppendAllText(_tablesLogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void TablesLogSeparator()
        {
            try
            {
                string line = $"{Environment.NewLine}{'─',60} {DateTime.Now:HH:mm:ss}{Environment.NewLine}";
                lock (_tablesLogLock)
                    File.AppendAllText(_tablesLogPath, line, Encoding.UTF8);
            }
            catch { }
        }

        private void AppLog(string category, string message)
        {
            System.Diagnostics.Debug.WriteLine($"[{category}] {message}");
            // Консоль показываем только если пользователь сам её открыл
            if (_consoleEnabled && _consoleForm != null && !_consoleForm.IsDisposed)
                _consoleForm.Log(category, message);

            // В файл пишем ТОЛЬКО сообщения об обновлении и ошибки
            bool isError = category == "ОШИБКИ";
            bool isUpdateMsg = message.Contains("[Версия]")
                            || message.Contains("[Обновление]")
                            || message.Contains("[Update]")
                            || message.StartsWith("══"); // разделители

            if (!isUpdateMsg && !isError) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath));
                string line = $"[{DateTime.Now:HH:mm:ss.fff}][{category}] {message}";
                File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);

                if (isError)
                {
                    string errPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinHK3", "errors_log.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(errPath));
                    File.AppendAllText(errPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════
        //  EnsureConsoleVisible — открывает консоль если она ещё не открыта
        //  Используется при обновлении чтобы пользователь видел весь процесс
        // ═══════════════════════════════════════════════════
        private void EnsureConsoleVisible()
        {
            if (_consoleForm == null || _consoleForm.IsDisposed)
                _consoleForm = new ConsoleForm();
            _consoleEnabled = true;
            _consoleForm.Show();
            _consoleForm.BringToFront();
        }

        private readonly System.Windows.Forms.Timer overlayTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _snapTimer = new System.Windows.Forms.Timer();
        private readonly Dictionary<IntPtr, Point> _prevWinPos = new Dictionary<IntPtr, Point>();
        private readonly WebView2 webView = new WebView2 { Dock = DockStyle.Fill };

        // ── WinAPI ────────────────────────────────────────
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lp);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool rep);
        [DllImport("user32.dll")] static extern short GetKeyState(int nVk);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LLKProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LLMProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CallNextHookEx(IntPtr hhk, int nc, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte sc, uint fl, int ei);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc fn, IntPtr lp);
        [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT pt, uint flags);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
        private const int DWMWA_CLOAKED = 14;

        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint nFlags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr ho);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
        private const uint PW_CLIENTONLY = 0x1;
        private const uint PW_RENDERFULLCONTENT = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        private delegate IntPtr LLKProc(int nc, IntPtr wp, IntPtr lp);
        private readonly LLKProc _hookProc;
        private IntPtr _hookID = IntPtr.Zero;

        static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

        private static RECT GetClientBounds(IntPtr hwnd)
        {
            GetClientRect(hwnd, out RECT client);
            var origin = new POINT { X = 0, Y = 0 };
            ClientToScreen(hwnd, ref origin);
            return new RECT
            {
                Left = origin.X,
                Top = origin.Y,
                Right = origin.X + client.Right,
                Bottom = origin.Y + client.Bottom
            };
        }

        private void SilentClick(IntPtr hwnd, int clientX, int clientY)
        {
            IntPtr lp = MakeLParam(clientX, clientY);
            SendMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, lp);
            SendMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
            SendMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lp);
        }

        private void PasteValue(IntPtr hwnd, int clientX, int clientY, string value)
        {
            string normalized = value.Replace(',', '.');

            // ── Шаг 1: активируем окно без физического перемещения мыши ──
            // WM_ACTIVATE (WA_ACTIVE=1) + WM_SETFOCUS заставляют окно принять
            // клавиатурный фокус так же, как при реальном клике.
            PostMessage(hwnd, WM_ACTIVATE, (IntPtr)1, IntPtr.Zero);
            PostMessage(hwnd, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(15);

            // ── Шаг 2: ПКМ по центру стола (без движения курсора) ─────────
            // SendMessage (блокирующий!) — ждём пока 1win полностью обработает
            // клик и переключит фокус, только потом идём дальше.
            RECT tableRect = GetClientBounds(hwnd);
            int tableW = tableRect.Right - tableRect.Left;
            int tableH = tableRect.Bottom - tableRect.Top;
            int centerX = (int)(tableW * TABLE_CENTER_REL_X);
            int centerY = (int)(tableH * TABLE_CENTER_REL_Y);
            IntPtr centerLp = MakeLParam(centerX, centerY);
            SendMessage(hwnd, WM_RBUTTONDOWN, (IntPtr)0x0002 /*MK_RBUTTON*/, centerLp);
            SendMessage(hwnd, WM_RBUTTONUP, IntPtr.Zero, centerLp);
            Thread.Sleep(80); // ждём пока 1win отрисует реакцию на ПКМ и встанет в фокус

            // ── Шаг 3: клик по полю ввода ставки ─────────────────────────
            SilentClick(hwnd, clientX, clientY);
            Thread.Sleep(40);

            // ── Шаг 4: Ctrl+A — выделить всё содержимое поля ─────────────
            const uint WM_KEYDOWN_LOCAL = 0x0100;
            const uint WM_KEYUP_LOCAL = 0x0101;
            IntPtr VK_CONTROL = new IntPtr(0x11);
            IntPtr VK_A = new IntPtr(0x41);
            PostMessage(hwnd, WM_KEYDOWN_LOCAL, VK_CONTROL, (IntPtr)0x001D0001);
            PostMessage(hwnd, WM_KEYDOWN_LOCAL, VK_A, (IntPtr)0x001E0001);
            PostMessage(hwnd, WM_KEYUP_LOCAL, VK_A, (IntPtr)0xC01E0001);
            PostMessage(hwnd, WM_KEYUP_LOCAL, VK_CONTROL, (IntPtr)0xC01D0001);
            Thread.Sleep(10);

            // ── Шаг 5: посылаем символы сайзинга без задержек ────────────
            foreach (char c in normalized)
                PostMessage(hwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);

            Thread.Sleep(10);

            // ── Шаг 6: Enter для подтверждения ───────────────────────────
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)0x0D, (IntPtr)0x001C0001);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)0x0D, (IntPtr)0xC01C0001);
        }

        private void ExecuteAction(DynamicAction action)
        {
            IntPtr hwnd = GetWindowUnderCursor();
            if (hwnd == IntPtr.Zero) return;
            RECT r = GetClientBounds(hwnd);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (action.UseSize && isGlobalInputSet)
            {
                int cx = (int)(w * globalInputX), cy = (int)(h * globalInputY);
                var t = new Thread(() => PasteValue(hwnd, cx, cy, action.SizeValue));
                t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(4000);
            }
            else if (!action.UseSize && action.IsSet)
            {
                if (_quickBetActionIds.Contains(action.InternalID))
                {
                    // ── Быстрая ставка: клик по таргету этой кнопки, затем клик по Raise ──
                    // Шаг 1: клик по координатам этой кнопки (сайз-точка на панели 1win)
                    SilentClick(hwnd, (int)(w * action.RelX), (int)(h * action.RelY));
                    // Шаг 2: пауза — такая же как при ПКМ+бинд (80ms), чтобы рум успел обработать
                    Thread.Sleep(80);
                    // Шаг 3: найти базовую кнопку Raise и кликнуть по ней
                    var raiseAction = userActions.FirstOrDefault(a =>
                        a.IsBase &&
                        string.Equals(a.DisplayName, "Raise", StringComparison.OrdinalIgnoreCase) &&
                        a.IsSet);
                    if (raiseAction != null)
                        SilentClick(hwnd, (int)(w * raiseAction.RelX), (int)(h * raiseAction.RelY));
                }
                else
                {
                    SilentClick(hwnd, (int)(w * action.RelX), (int)(h * action.RelY));
                }
            }
        }

        // ═══════════════════════════════════════════════════
        //  КОНСТРУКТОР
        // ═══════════════════════════════════════════════════
        public Form1()
        {
            Text = $"1WIN TOOLS PRO  v{CURRENT_VERSION}";
            Size = new Size(760, 600);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(14, 14, 22);
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            // Скрываем форму ДО первого показа — иначе фиолетовый фон мелькает
            // поверх окна авторизации (TopMost = true). Show() вызывается только
            // после успешной проверки лицензии в Load-handler.
            Opacity = 0;
            ShowInTaskbar = false;
            _hookProc = HookCallback;

            if (!Directory.Exists(configsFolder)) Directory.CreateDirectory(configsFolder);

            Controls.Add(webView);

            Load += async (s, e) => {
                try
                {
                    Hide();
                    // Останавливаем таймер overlay на время проверок — иначе он делает overlay.Visible=true
                    // каждые 200мс и перекрывает окно авторизации
                    overlayTimer.Stop();

                    // ШАГ 1: ВЕРСИЯ — жёсткая блокировка, до показа UI
                    bool versionOk = await EnforceVersionCheck();
                    if (!versionOk) { Application.Exit(); return; }

                    // ШАГ 2: ЛИЦЕНЗИЯ
                    if (!LicenseManager.CheckOnStartup()) { Application.Exit(); return; }

                    // ШАГ 3: Только если всё прошло — показываем overlay и инициализируем WebView
                    overlayTimer.Start(); // запускаем таймер только после успешной лицензии
                    overlay.Show(); // показываем overlay только ПОСЛЕ лицензии
                    await InitWebView();
                    Opacity = 1;          // восстанавливаем видимость формы
                    ShowInTaskbar = true; // возвращаем в панель задач
                    Show();
                }
                catch (Exception startEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[STARTUP] Fatal: {startEx}");
                    MessageBox.Show(
                        $"Критическая ошибка при запуске:\n{startEx.Message}",
                        "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                }
            };

            LoadBaseActionsFromGeneral();
            LoadBaseState();
            if (File.Exists(autoSavePath)) LoadDataFromFile(autoSavePath);
            LoadLayoutsFromFile();
            LoadTableSize();
            LoadConverterSettings();
            LoadH2NSettings();
            LoadH2NSettingsT2();
            LoadAutoStartSettings();
            T2LogInit();
            System.Threading.Tasks.Task.Run(() => { LoadFishCache(); LoadRegCache(); });

            // ── Создаём папку AllHands для архива раздач ──────────
            try { Directory.CreateDirectory(AllHandsDir); } catch { }
            _converter.AllHandsDir = AllHandsDir;

            // Очищаем лог-файл при каждом старте
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath));
                File.WriteAllText(_logFilePath, $"=== WinHK3 запущен {DateTime.Now:dd.MM.yyyy HH:mm:ss} ==={Environment.NewLine}", Encoding.UTF8);
            }
            catch { }

            // Инициализируем tables_log.txt в папке программы
            try
            {
                File.WriteAllText(_tablesLogPath,
                    $"=== WinHK3 TABLES LOG — запуск {DateTime.Now:dd.MM.yyyy HH:mm:ss} ==={Environment.NewLine}" +
                    $"Папка программы: {AppDomain.CurrentDomain.BaseDirectory}{Environment.NewLine}" +
                    $"Системное время: {DateTime.Now:HH:mm:ss} (Local) / {DateTime.UtcNow:HH:mm:ss} (UTC){Environment.NewLine}" +
                    $"Часовой пояс: {TimeZoneInfo.Local.DisplayName}{Environment.NewLine}" +
                    Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }

            overlayTimer.Interval = 200;
            overlayTimer.Tick += (s, e) => { bool changed = UpdateOverlay(); if (changed) overlay.Invalidate(); };

            // Таймер мониторинга активных столов (каждые 10 сек)
            _tableActivityTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _tableActivityTimer.Tick += (s, e) => {
                PushTables2Update();
                if (_autoLayoutEnabled && activeLayout != null && activeLayout.Slots.Count > 0)
                    _ = UpdateLayoutPreviewInUI(activeLayout);
            };
            _tableActivityTimer.Start();

            // ── Debounce-таймер для FSW маркеров (300 мс) ────────────────────
            _t2CmDebounceTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _t2CmDebounceTimer.Tick += (s, e) =>
            {
                _t2CmDebounceTimer.Stop();
                if (!_t2CmChangePending) return;
                _t2CmChangePending = false;
                _t2H2nReader.InvalidateCache();
                _t2RegReader.InvalidateCache();
                lock (_fishCacheLock) { _fishCache.Clear(); }
                lock (_regCacheLock) { _regCache.Clear(); }
                T2Log("[FSW] .cm changed → cache cleared");
                System.Threading.Tasks.Task.Run(() => PushTables2Update());
            };

            // Единый таймер сканирования окон (300 мс) — питает TableManager,
            // AutoLayoutTick и CheckSnapWindows берут данные отсюда.
            _masterScanTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _masterScanTimer.Tick += (s, e) => RefreshTableWindows();
            _masterScanTimer.Start();

            // ── Fish Monitor ──────────────────────────────────
            _fishMonitor = new FishMonitor(_h2nReader);
            _fishMonitor.OnNoFishAlert += OnFishMonitorAlert;
            // overlayTimer.Start() перенесён в Load-handler, после проверки лицензии
            // чтобы таймер не вызывал overlay.Visible=true во время показа окна авторизации

            // _snapTimer использует данные из _masterScanTimer — запускаем с тем же интервалом
            _snapTimer.Interval = 300;
            _snapTimer.Tick += (s, e) => CheckSnapWindows();
            _snapTimer.Start();

            _autoSeat = new AutoSeatManager(
                getTableHwnd: () => GetPrimaryTableHwnd(),
                silentClick: SilentClick,
                log: (msg) => AppLog("РУМ", msg)
            );

            _autoLayoutTimer.Interval = 400;
            _autoLayoutTimer.Tick += (s, e) => AutoLayoutTick();

            overlay.OnPointMoved = (pt, rx, ry) => {
                if (pt.IsInput)
                {
                    globalInputX = rx; globalInputY = ry;
                    isGlobalInputSet = true; SaveDataToFile(autoSavePath);
                    AppLog("ДЕЙСТВИЯ", $"INPUT перемещён: {(int)(rx * 100)}% {(int)(ry * 100)}%");
                }
                else
                {
                    var a = userActions.FirstOrDefault(x => x.InternalID == pt.ActionId);
                    if (a != null)
                    {
                        a.RelX = rx; a.RelY = ry;
                        SaveDataToFile(autoSavePath);
                        overlay.DebugPoints.Clear();
                        AppLog("ДЕЙСТВИЯ", $"TARGET [{a.DisplayName}] перемещён: {(int)(rx * 100)}% {(int)(ry * 100)}%");
                    }
                }
            };

            // overlay.Show() перенесён выше в Load-handler, после проверки лицензии
        }

        // ═══════════════════════════════════════════════════
        //  WEBVIEW2 ИНИЦИАЛИЗАЦИЯ
        // ═══════════════════════════════════════════════════
        private async Task InitWebView()
        {
            await webView.EnsureCoreWebView2Async();

            var webSettings = webView.CoreWebView2.Settings;
            webSettings.AreDevToolsEnabled = false;
            webSettings.AreDefaultContextMenusEnabled = false;
            webSettings.AreBrowserAcceleratorKeysEnabled = false;
            webSettings.IsStatusBarEnabled = false;
            webSettings.IsZoomControlEnabled = false;
            webSettings.IsWebMessageEnabled = true;
            webSettings.AreHostObjectsAllowed = true;

            webView.CoreWebView2.NavigationStarting += (navSender, navArgs) =>
            {
                string uri = navArgs.Uri ?? "";
                bool allowed = uri.StartsWith("https://app.local/", StringComparison.OrdinalIgnoreCase)
                            || uri == "about:blank"
                            || string.IsNullOrEmpty(uri);
                if (!allowed)
                {
                    navArgs.Cancel = true;
                    AppLog("БЕЗОПАСНОСТЬ", $"Заблокирована навигация: {uri}");
                }
            };

            webView.CoreWebView2.NewWindowRequested += (nwSender, nwArgs) =>
            {
                nwArgs.Handled = true;
                AppLog("БЕЗОПАСНОСТЬ", $"Заблокировано новое окно: {nwArgs.Uri}");
            };

            webView.CoreWebView2.ScriptDialogOpening += (sdSender, sdArgs) =>
            {
                sdArgs.Accept();
            };

            webView.CoreWebView2.PermissionRequested += (prSender, prArgs) =>
            {
                prArgs.State = CoreWebView2PermissionState.Deny;
            };

            // ui.html загружается из встроенного ресурса.
            // SetVirtualHostNameToFolderMapping нужен для относительных ресурсов (картинки, шрифты).
            // Если их нет — строка ниже не мешает, оставляем для совместимости.
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local", appDir,
                CoreWebView2HostResourceAccessKind.Allow);

            webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            webView.CoreWebView2.DOMContentLoaded += async (s, e) => {
                await Task.Delay(200);
                await SyncStateToUI();
                AppLog("ОБЩЕЕ", "1WIN TOOLS PRO запущен");
                AppLog("ОБЩЕЕ", $"Загружено действий: {userActions.Count}");
                AppLog("ОБЩЕЕ", $"Лейаутов: {layouts.Count}");
            };

            // Распаковываем ui.html из Embedded Resource во временный файл в AppData
            // Это сохраняет работу fetch(), относительных путей и app.local маппинга
            string extractedHtml = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WinHK3", "ui.html");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(extractedHtml));
                File.WriteAllText(extractedHtml, LoadEmbeddedHtml(), Encoding.UTF8);
            }
            catch { }

            // Переопределяем маппинг на папку AppData\WinHK3\ где лежит распакованный ui.html
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinHK3"),
                CoreWebView2HostResourceAccessKind.Allow);

            if (File.Exists(extractedHtml))
                webView.CoreWebView2.Navigate("https://app.local/ui.html");
            else
                webView.CoreWebView2.NavigateToString(FallbackHtml());
        }

        // ─────────────────────────────────────────────────
        //  Обработка сообщений от JS → C#
        // ─────────────────────────────────────────────────
        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string rawJson = e.WebMessageAsJson;
                if (rawJson.StartsWith("\""))
                    rawJson = System.Text.Json.JsonSerializer.Deserialize<string>(rawJson);
                var root = JObject.Parse(rawJson);
                string cmd = root["cmd"]?.Value<string>() ?? "";

                switch (cmd)
                {
                    case "dragWindow":
                        {
                            int ddx = root["dx"]?.Value<int>() ?? 0;
                            int ddy = root["dy"]?.Value<int>() ?? 0;
                            ddx = Math.Max(-500, Math.Min(500, ddx));
                            ddy = Math.Max(-500, Math.Min(500, ddy));
                            if (ddx != 0 || ddy != 0)
                                this.BeginInvoke(new Action(() => {
                                    var screens = Screen.AllScreens;
                                    int newX = Math.Max(-Width + 50, Math.Min(screens.Max(s => s.Bounds.Right) - 50, Location.X + ddx));
                                    int newY = Math.Max(0, Math.Min(screens.Max(s => s.Bounds.Bottom) - 50, Location.Y + ddy));
                                    Location = new Point(newX, newY);
                                }));
                            break;
                        }

                    case "togglePower":
                        this.BeginInvoke(new Action(() => TogglePower()));
                        break;

                    case "close":
                        this.BeginInvoke(new Action(() => Close()));
                        break;
                    case "minimize":
                        this.BeginInvoke(new Action(() => WindowState = FormWindowState.Minimized));
                        break;

                    case "addAction":
                        this.BeginInvoke(new Action(() =>
                        {
                            var a = new DynamicAction { DisplayName = "Action", InternalID = idCounter++ };
                            userActions.Add(a);
                            SaveDataToFile(autoSavePath);
                            AppLog("ДЕЙСТВИЯ", $"Добавлено действие: {a.DisplayName} (id={a.InternalID})");
                            _ = SyncActionsToUI();
                        }));
                        break;

                    case "syncActions":
                        this.BeginInvoke(new Action(() => {
                            SyncActionsFromJS(root);
                        }));
                        break;

                    case "deleteAction":
                        {
                            int delId = root["id"]?.Value<int>() ?? -1;
                            this.BeginInvoke(new Action(() =>
                            {
                                var toRemove = userActions.FirstOrDefault(a => a.InternalID == delId);
                                if (toRemove == null || toRemove.IsBase) return;
                                string rName = toRemove.DisplayName;
                                userActions.RemoveAll(a => a.InternalID == delId);
                                SaveDataToFile(autoSavePath);
                                AppLog("ДЕЙСТВИЯ", $"Удалено действие: {rName}");
                            }));
                            break;
                        }

                    case "bindKey":
                        {
                            int bindId = root["id"]?.Value<int>() ?? -1;
                            this.BeginInvoke(new Action(() => {
                                var ba = userActions.FirstOrDefault(x => x.InternalID == bindId);
                                AppLog("ДЕЙСТВИЯ", $"Ожидание клавиши для: {ba?.DisplayName ?? $"id={bindId}"}");
                                StartKeyBinding(bindId);
                            }));
                            break;
                        }

                    case "setTarget":
                        {
                            int targetId = root["id"]?.Value<int>() ?? -1;
                            var ta = userActions.FirstOrDefault(x => x.InternalID == targetId);
                            AppLog("ДЕЙСТВИЯ", $"Установка таргета для: {ta?.DisplayName ?? $"id={targetId}"}");
                            Task.Run(async () => await StartLearning(targetId));
                            break;
                        }

                    case "cancelBind":
                        this.BeginInvoke(new Action(() => {
                            _bindingForId = -1;
                            AppLog("ДЕЙСТВИЯ", "Привязка клавиши отменена");
                        }));
                        break;

                    case "cancelTarget":
                        this.BeginInvoke(new Action(() => {
                            StopMouseCapture();
                            AppLog("ДЕЙСТВИЯ", "Таргет отменён (Escape)");
                        }));
                        break;

                    case "resetTarget":
                        {
                            int rtId = root["id"]?.Value<int>() ?? -1;
                            var rta = userActions.FirstOrDefault(x => x.InternalID == rtId);
                            if (rta != null)
                            {
                                rta.RelX = 0; rta.RelY = 0; rta.IsSet = false;
                                SaveDataToFile(autoSavePath);
                                AppLog("ДЕЙСТВИЯ", $"Таргет сброшен: {rta.DisplayName}");
                            }
                            break;
                        }

                    case "setInput":
                        AppLog("ДЕЙСТВИЯ", "Установка поля ввода (клик по окну)");
                        Task.Run(async () => await CaptureGlobalInput());
                        break;

                    case "applyLayout":
                        this.BeginInvoke(new Action(() => {
                            AppLog("ДЕЙСТВИЯ", $"Применён preset лейаут");
                            ApplyLayoutToWindows();
                        }));
                        break;
                    case "toggleAutoLayout":
                        this.BeginInvoke(new Action(() => {
                            _autoLayoutEnabled = !_autoLayoutEnabled;
                            if (_autoLayoutEnabled)
                            {
                                _hwndSlotMap.Clear();
                                _hwndMovedAt.Clear();
                                _hwndTargetRect.Clear();
                                _autoLayoutTimer.Start();
                                AppLog("РУМ", "Постоянная расстановка: ВКЛЮЧЕНА");
                            }
                            else
                            {
                                _autoLayoutTimer.Stop();
                                AppLog("РУМ", "Постоянная расстановка: выключена");
                            }
                            _ = PostToJS($"setAutoLayoutState({_autoLayoutEnabled.ToString().ToLower()})");
                        }));
                        break;
                    case "applyNamedLayout":
                        {
                            string lname = root["name"]?.Value<string>() ?? "";
                            this.BeginInvoke(new Action(async () => {
                                var nl = layouts.FirstOrDefault(l => l.Name == lname);
                                if (nl != null)
                                {
                                    activeLayout = nl;
                                    ApplyLayoutToWindows();
                                    AppLog("ДЕЙСТВИЯ", $"Применён лейаут: {lname}");
                                    await UpdateLayoutPreviewInUI(activeLayout);
                                }
                                else AppLog("ОШИБКИ", $"Лейаут не найден: {lname}");
                            }));
                            break;
                        }
                    case "editGrid":
                        this.BeginInvoke(new Action(() => OpenLayoutEditor()));
                        break;
                    case "saveLayout":
                        this.BeginInvoke(new Action(() => SaveLayoutsToFile()));
                        break;
                    case "deleteLayout":
                        {
                            string delName = root["name"]?.Value<string>() ?? "";
                            this.BeginInvoke(new Action(() =>
                            {
                                var toDelete = layouts.FirstOrDefault(l => l.Name == delName);
                                if (toDelete != null)
                                {
                                    layouts.Remove(toDelete);
                                    if (activeLayout == toDelete) activeLayout = layouts.Count > 0 ? layouts[0] : new LayoutConfig();
                                    SaveLayoutsToFile();
                                    _ = RefreshLayoutListInUI(false);
                                    AppLog("ДЕЙСТВИЯ", $"Лейаут удалён: {delName}");
                                }
                                else AppLog("ОШИБКИ", $"Лейаут не найден: {delName}");
                            }));
                            break;
                        }

                    case "saveCurrentLayout":
                        {
                            string newLayoutName = SanitizeFileName(root["name"]?.Value<string>() ?? "");
                            if (!string.IsNullOrWhiteSpace(newLayoutName))
                            {
                                this.BeginInvoke(new Action(() => SaveCurrentWindowLayout(newLayoutName)));
                            }
                            break;
                        }

                    case "saveCfg":
                        {
                            string saveName = root["name"]?.Value<string>() ?? "";
                            saveName = SanitizeFileName(saveName);
                            if (root["actions"] is JArray) SyncActionsFromJS(root);
                            if (!string.IsNullOrWhiteSpace(saveName))
                            {
                                this.BeginInvoke(new Action(() =>
                                {
                                    SaveDataToFile(Path.Combine(configsFolder, saveName + ".txt"));
                                    AppLog("ДЕЙСТВИЯ", $"Конфиг сохранён: {saveName}");
                                    _ = PostToJS($"setLog({JsonConvert.SerializeObject("Сохранено: " + saveName)}, '#22c55e')");
                                    _ = RefreshCfgListInUI();
                                }));
                            }
                            break;
                        }
                    case "loadCfg":
                        {
                            string loadName = SanitizeFileName(root["name"]?.Value<string>() ?? "");
                            string loadPath = Path.Combine(configsFolder, loadName + ".txt");
                            if (File.Exists(loadPath))
                            {
                                this.BeginInvoke(new Action(() =>
                                {
                                    LoadDataFromFile(loadPath);
                                    AppLog("ДЕЙСТВИЯ", $"Конфиг загружен: {loadName}");
                                    _ = SyncActionsToUI();
                                    _ = PostToJS($"setLog({JsonConvert.SerializeObject("Загружено: " + loadName)}, '#22c55e')");
                                }));
                            }
                            break;
                        }
                    case "loadCfgDialog":
                        this.BeginInvoke(new Action(() =>
                        {
                            using (var dlg = new OpenFileDialog
                            {
                                Title = "Выберите файл конфига",
                                Filter = "Config|*.txt|Все|*.*",
                                InitialDirectory = Directory.Exists(configsFolder) ? configsFolder : AppDomain.CurrentDomain.BaseDirectory
                            })
                            {
                                if (dlg.ShowDialog() != DialogResult.OK) return;
                                string cfgName = SanitizeFileName(Path.GetFileNameWithoutExtension(dlg.FileName));
                                string destPath = Path.Combine(configsFolder, cfgName + ".txt");
                                if (!string.Equals(dlg.FileName, destPath, StringComparison.OrdinalIgnoreCase))
                                    File.Copy(dlg.FileName, destPath, overwrite: true);
                                LoadDataFromFile(destPath);
                                AppLog("ДЕЙСТВИЯ", $"Конфиг загружен из файла: {dlg.FileName}");
                                _ = SyncActionsToUI();
                                _ = RefreshCfgListInUI();
                                _ = PostToJS($"setLog({JsonConvert.SerializeObject("Загружено: " + cfgName)}, '#22c55e')");
                            }
                        }));
                        break;
                    case "delCfg":
                        {
                            string delName = SanitizeFileName(root["name"]?.Value<string>() ?? "");
                            string delPath = Path.Combine(configsFolder, delName + ".txt");
                            if (File.Exists(delPath))
                            {
                                this.BeginInvoke(new Action(() =>
                                {
                                    File.Delete(delPath);
                                    AppLog("ДЕЙСТВИЯ", $"Конфиг удалён: {delName}");
                                    _ = RefreshCfgListInUI();
                                    _ = PostToJS("setLog('Удалено', '#ef4444')");
                                }));
                            }
                            break;
                        }

                    case "openTestZone":
                        this.BeginInvoke(new Action(() => OpenTestZone()));
                        break;
                    case "toggleLang":
                        this.BeginInvoke(new Action(() => isRussian = !isRussian));
                        break;
                    case "toggleDebug":
                        this.BeginInvoke(new Action(() =>
                        {
                            overlay.DebugMode = !overlay.DebugMode;
                            AppLog("ДЕЙСТВИЯ", $"Debug Overlay: {(overlay.DebugMode ? "включён" : "выключен")}");
                            SyncHotkeyEntriesToOverlay();
                            SaveAutoStartSettings();
                            _ = PostToJS($"setDebugState({overlay.DebugMode.ToString().ToLower()})");
                        }));
                        break;

                    case "toggleTableBorder":
                        this.BeginInvoke(new Action(() =>
                        {
                            _tableBorderEnabled = !_tableBorderEnabled;
                            overlay.ShowBorder = _tableBorderEnabled;
                            overlay.Invalidate();
                            SaveAutoStartSettings();
                            AppLog("ДЕЙСТВИЯ", $"Обводка стола: {(_tableBorderEnabled ? "вкл" : "выкл")}");
                            string bColorHex = $"#{_tableBorderColor.R:X2}{_tableBorderColor.G:X2}{_tableBorderColor.B:X2}";
                            _ = PostToJS($"setTableBorderState({_tableBorderEnabled.ToString().ToLower()}, '{bColorHex.ToLower()}')");
                        }));
                        break;

                    case "setTableBorderColor":
                        {
                            string hex = root["color"]?.Value<string>() ?? "#6366f1";
                            this.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    // hex вида #rrggbb
                                    int r2 = Convert.ToInt32(hex.Substring(1, 2), 16);
                                    int g2 = Convert.ToInt32(hex.Substring(3, 2), 16);
                                    int b2 = Convert.ToInt32(hex.Substring(5, 2), 16);
                                    _tableBorderColor = Color.FromArgb(r2, g2, b2);
                                    overlay.BorderColor = _tableBorderColor;
                                    overlay.Invalidate();
                                    SaveAutoStartSettings();
                                    AppLog("ДЕЙСТВИЯ", $"Цвет обводки: {hex}");
                                }
                                catch { }
                            }));
                        }
                        break;

                    case "toggleQuickBet":
                        {
                            int qbId = root["id"]?.Value<int>() ?? -1;
                            this.BeginInvoke(new Action(() =>
                            {
                                if (_quickBetActionIds.Contains(qbId))
                                    _quickBetActionIds.Remove(qbId);
                                else
                                    _quickBetActionIds.Add(qbId);
                                SaveAutoStartSettings();
                                AppLog("ДЕЙСТВИЯ", $"Быстрая ставка: ids=[{string.Join(",", _quickBetActionIds)}]");
                                _ = PostToJS($"setQuickBetState({JsonConvert.SerializeObject(_quickBetActionIds)})");
                            }));
                        }
                        break;

                    case "setShowBinds":
                        {
                            bool sbVal = root["value"]?.Value<bool>() ?? true;
                            this.BeginInvoke(new Action(() => {
                                overlay.ShowBinds = sbVal;
                                SyncHotkeyEntriesToOverlay();
                                overlay.Invalidate();
                                AppLog("ДЕЙСТВИЯ", $"Показ биндов: {(sbVal ? "вкл" : "выкл")}");
                            }));
                        }
                        break;
                    case "openTelegram":
                        this.BeginInvoke(new Action(() => Process.Start(SUPPORT_URL)));
                        break;
                    case "toggleConsole":
                        this.BeginInvoke(new Action(() => {
                            _consoleEnabled = !_consoleEnabled;
                            if (_consoleEnabled)
                            {
                                if (_consoleForm == null || _consoleForm.IsDisposed)
                                    _consoleForm = new ConsoleForm();
                                _consoleForm.Show();
                                _consoleForm.BringToFront();
                                AppLog("ОБЩЕЕ", "Консоль включена");
                            }
                            else
                            {
                                AppLog("ОБЩЕЕ", "Консоль скрыта");
                                _consoleForm?.Hide();
                            }
                            _ = PostToJS($"setConsoleState({(_consoleEnabled ? "true" : "false")})");
                        }));
                        break;
                    case "updateWindowTitle":
                        this.BeginInvoke(new Action(() => {
                            string rawTitle = root["value"]?.Value<string>() ?? partialWindowTitle;
                            if (!string.IsNullOrEmpty(rawTitle) && rawTitle.Length <= 128
                                && rawTitle.All(c => !char.IsControl(c)))
                            {
                                partialWindowTitle = rawTitle;
                                AppLog("ДЕЙСТВИЯ", $"Заголовок окна: {partialWindowTitle}");
                            }
                        }));
                        break;

                    case "debugWindowInfo":
                        this.BeginInvoke(new Action(() => {
                            int found = 0;
                            EnumWindows((hWnd, lp) => {
                                var sbw = new StringBuilder(256); GetWindowText(hWnd, sbw, 256);
                                string wt = sbw.ToString();
                                if (IsTableWindow(wt))
                                {
                                    found++;
                                    GetWindowRect(hWnd, out RECT wrc);
                                    RECT cbc = GetClientBounds(hWnd);
                                    AppLog("РЕАКЦИИ", $"── WIN #{found}  0x{hWnd.ToInt64():X8}");
                                    AppLog("РЕАКЦИИ", $"  Title      : \"{wt}\"");
                                    AppLog("РЕАКЦИИ", $"  WindowRect : ({wrc.Left},{wrc.Top})  {wrc.Right - wrc.Left}×{wrc.Bottom - wrc.Top}");
                                    AppLog("РЕАКЦИИ", $"  ClientRect : ({cbc.Left},{cbc.Top})  {cbc.Right - cbc.Left}×{cbc.Bottom - cbc.Top}");
                                    AppLog("РЕАКЦИИ", $"  Рамка      : L={cbc.Left - wrc.Left} T={cbc.Top - wrc.Top} R={wrc.Right - cbc.Right} B={wrc.Bottom - cbc.Bottom}");
                                }
                                return true;
                            }, IntPtr.Zero);
                            string msg2 = found > 0 ? $"Найдено {found} окн(о/а)" : $"Окна НЕ найдены! Заголовок: \"{partialWindowTitle}\"";
                            AppLog("РЕАКЦИИ", msg2);
                            _ = PostToJS($"setLog({JsonConvert.SerializeObject(msg2)}, '{(found > 0 ? "#22c55e" : "#ef4444")}')");
                            if (!_consoleEnabled)
                            {
                                _consoleEnabled = true;
                                if (_consoleForm == null || _consoleForm.IsDisposed) _consoleForm = new ConsoleForm();
                                _consoleForm.Show(); _consoleForm.BringToFront();
                                _ = PostToJS("setConsoleState(true)");
                            }
                        }));
                        break;
                    case "showLicense":
                        this.BeginInvoke(new Action(() => LicenseManager.CheckOnStartup()));
                        break;

                    case "setSnapEnabled":
                        {
                            bool sv = root["value"]?.Value<bool>() ?? true;
                            this.BeginInvoke(new Action(() => {
                                snapEnabled = sv;
                                AppLog("ДЕЙСТВИЯ", $"Snap: {(snapEnabled ? "вкл" : "выкл")}");
                            }));
                        }
                        break;

                    case "setSwapEnabled":
                        {
                            bool sv2 = root["value"]?.Value<bool>() ?? true;
                            this.BeginInvoke(new Action(() => {
                                swapEnabled = sv2;
                                AppLog("ДЕЙСТВИЯ", $"Swap: {(swapEnabled ? "вкл" : "выкл")}");
                            }));
                        }
                        break;

                    case "setAutoStartMaster":
                        // master убран — игнорируем команду для обратной совместимости с UI
                        break;

                    case "setAutoStartConverter":
                        {
                            bool v = root["value"]?.Value<bool>() ?? false;
                            this.BeginInvoke(new Action(() => {
                                _autoStartConverter = v;
                                SaveAutoStartSettings();
                                AppLog("ДЕЙСТВИЯ", $"AutoStart конвертор: {(v ? "вкл" : "выкл")}");
                            }));
                        }
                        break;

                    case "setAutoStartLayout":
                        {
                            bool v = root["value"]?.Value<bool>() ?? false;
                            this.BeginInvoke(new Action(() => {
                                _autoStartLayout = v;
                                SaveAutoStartSettings();
                                AppLog("ДЕЙСТВИЯ", $"AutoStart лейаут: {(v ? "вкл" : "выкл")}");
                            }));
                        }
                        break;

                    case "setAutoStartLayoutName":
                        {
                            string lname = root["name"]?.Value<string>() ?? "";
                            this.BeginInvoke(new Action(() => {
                                _autoStartLayoutName = lname;
                                SaveAutoStartSettings();
                                AppLog("ДЕЙСТВИЯ", $"AutoStart лейаут выбран: {lname}");
                            }));
                        }
                        break;

                    case "startTableDetect":
                        this.BeginInvoke(new Action(() => StartTableDetect()));
                        break;

                    case "clearTableSize":
                        _tableW = 0; _tableH = 0; _detectStep = 0;
                        AppLog("ДЕЙСТВИЯ", "Размер стола сброшен");
                        break;

                    case "updateHotkeyOverlay":
                        {
                            this.BeginInvoke(new Action(() => {
                                overlay.HotkeyEntries.Clear();
                                if (root["actions"] is JArray arr2)
                                {
                                    foreach (JObject item in arr2.OfType<JObject>())
                                    {
                                        overlay.HotkeyEntries.Add(new OverlayForm.HotkeyEntry
                                        {
                                            Name = item["name"]?.Value<string>() ?? "",
                                            Key = item["key"]?.Value<string>() ?? "—",
                                            Enabled = item["enabled"]?.Value<bool>() ?? true
                                        });
                                    }
                                }
                                overlay.Invalidate();
                            }));
                            break;
                        }
                    case "screenBrowseFolder":
                        this.BeginInvoke(new Action(() =>
                        {
                            using (var dlg = new FolderBrowserDialog())
                            {
                                dlg.Description = "Выберите папку для сохранения скриншотов";
                                if (!string.IsNullOrEmpty(_screenSaveFolder) && Directory.Exists(_screenSaveFolder))
                                    dlg.SelectedPath = _screenSaveFolder;
                                if (dlg.ShowDialog() == DialogResult.OK)
                                {
                                    _screenSaveFolder = dlg.SelectedPath;
                                    _ = PostToJS($"setScreenFolder({JsonConvert.SerializeObject(_screenSaveFolder)})");
                                    AppLog("ОБЩЕЕ", $"Screen: папка выбрана → {_screenSaveFolder}");
                                }
                            }
                        }));
                        break;

                    case "screenRefreshTables":
                        this.BeginInvoke(new Action(() =>
                        {
                            var titles = GetAllTableTitles();
                            var json = JsonConvert.SerializeObject(titles);
                            _ = PostToJS($"setScreenTables({json})");
                        }));
                        break;

                    case "screenStart":
                        {
                            int ms = root["intervalMs"]?.Value<int>() ?? 1000;
                            string fmt = root["format"]?.Value<string>() ?? "jpg";
                            int qual = root["quality"]?.Value<int>() ?? 85;
                            var tablesToken = root["tables"] as JArray;
                            var tables = tablesToken?.Select(t => t.Value<string>()).ToList() ?? new List<string> { "all" };
                            this.BeginInvoke(new Action(() => StartScreenCapture(ms, fmt, qual, tables)));
                            break;
                        }

                    case "screenStop":
                        this.BeginInvoke(new Action(() => StopScreenCapture()));
                        break;

                    case "converterBrowseInput":
                        this.BeginInvoke(new Action(() =>
                        {
                            using (var dlg = new FolderBrowserDialog { Description = "Выберите папку входа (Input)" })
                            {
                                if (!string.IsNullOrEmpty(_converter.InputDir) && Directory.Exists(_converter.InputDir))
                                    dlg.SelectedPath = _converter.InputDir;
                                if (dlg.ShowDialog() == DialogResult.OK)
                                {
                                    _converter.InputDir = dlg.SelectedPath;
                                    SaveConverterSettings();
                                    _ = PostToJS($"converterSetInput({JsonConvert.SerializeObject(_converter.InputDir)})");
                                    AppLog("РУМ", $"Converter INPUT: {_converter.InputDir}");
                                    TablesLog($"InputDir задан пользователем: '{_converter.InputDir}' | Существует: {Directory.Exists(_converter.InputDir)}");
                                }
                            }
                        }));
                        break;

                    case "converterBrowseOutput":
                        this.BeginInvoke(new Action(() =>
                        {
                            using (var dlg = new FolderBrowserDialog { Description = "Выберите папку выхода (Output)" })
                            {
                                if (!string.IsNullOrEmpty(_converter.OutputDir) && Directory.Exists(_converter.OutputDir))
                                    dlg.SelectedPath = _converter.OutputDir;
                                if (dlg.ShowDialog() == DialogResult.OK)
                                {
                                    _converter.OutputDir = dlg.SelectedPath;
                                    SaveConverterSettings();
                                    _ = PostToJS($"converterSetOutput({JsonConvert.SerializeObject(_converter.OutputDir)})");
                                    AppLog("РУМ", $"Converter OUTPUT: {_converter.OutputDir}");
                                }
                            }
                        }));
                        break;

                    case "converterStartLive":
                        this.BeginInvoke(new Action(() =>
                        {
                            if (string.IsNullOrEmpty(_converter.InputDir) || string.IsNullOrEmpty(_converter.OutputDir))
                            {
                                _ = PostToJS("converterLog('Выберите папки Input и Output!', '#ef4444')");
                                return;
                            }
                            _converter.LoadCache();
                            _converter.StartLive(msg =>
                            {
                                _ = PostToJS($"converterLog({JsonConvert.SerializeObject(msg)}, '#22c55e')");
                                AppLog("РУМ", $"[Converter] {msg}");
                            });
                            _ = PostToJS("converterOnStarted()");
                            AppLog("РУМ", "Converter LIVE запущен");
                        }));
                        break;

                    case "converterStopLive":
                        this.BeginInvoke(new Action(() =>
                        {
                            _converter.StopLive();
                            _ = PostToJS("converterOnStopped()");
                            AppLog("РУМ", "Converter LIVE остановлен");
                        }));
                        break;

                    case "converterBatchConvert":
                        this.BeginInvoke(new Action(() =>
                        {
                            if (string.IsNullOrEmpty(_converter.InputDir) || string.IsNullOrEmpty(_converter.OutputDir))
                            {
                                _ = PostToJS("converterLog('Выберите папки Input и Output!', '#ef4444')");
                                return;
                            }
                            int cnt = _converter.RunBatchConvert();
                            string msg = $"Обработано файлов: {cnt}";
                            _ = PostToJS($"converterLog({JsonConvert.SerializeObject(msg)}, '#22c55e')");
                            AppLog("РУМ", $"[Converter] Batch: {msg}");
                        }));
                        break;

                    case "getTables2":
                        _ = System.Threading.Tasks.Task.Run(() => PushTables2Update());
                        break;

                    // ── H2N Fish Detection ────────────────────────────────────────────
                    case "setH2nPath":
                        {
                            string h2nPath = root["value"]?.Value<string>() ?? "";
                            _h2nReader.ColorMarkersPath = h2nPath;
                            _h2nReader.InvalidateCache();
                            _fishMonitor?.ResetAll();
                            // Сбрасываем кэш фишей при смене пути H2N
                            lock (_fishCacheLock) { _fishCache.Clear(); }
                            if (!string.IsNullOrEmpty(h2nPath))
                                System.Threading.Tasks.Task.Run(() => _h2nReader.PreloadAll());
                            SaveH2NSettings();
                            AppLog("СТОЛЫ", $"H2N ColorMarkers: {h2nPath}");
                        }
                        break;

                    case "setFishMarkerIds":
                        {
                            // Принимает JSON-массив объектов [{id, active, color}, ...]
                            string raw = root["value"]?.Value<string>() ?? "";
                            try
                            {
                                var arr = JArray.Parse(raw);
                                _h2nReader.MarkersFromJson(arr);
                            }
                            catch
                            {
                                // Fallback: plain строки через \n
                                var ids = raw.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries);
                                _h2nReader.SetFishMarkerIds(ids);
                            }
                            // Сбрасываем кэш — маркеры изменились
                            lock (_fishCacheLock) { _fishCache.Clear(); }
                            SaveH2NSettings();
                            AppLog("СТОЛЫ", $"Fish Markers: {_h2nReader.FishMarkers.Count} шт.");
                        }
                        break;

                    case "setAutoSitOut":
                        {
                            _autoSitOutEnabled = root["value"]?.Value<bool>() ?? false;
                            if (!_autoSitOutEnabled)
                                _fishMonitor?.ResetAll();
                            SaveH2NSettings();
                            AppLog("СТОЛЫ", $"Auto SitOut: {_autoSitOutEnabled}");
                        }
                        break;

                    // ── Tables 2: отдельные H2N настройки ─────────────────────────────
                    case "setT2H2nPath":
                        {
                            string path = root["value"]?.Value<string>() ?? "";
                            _t2H2nReader.ColorMarkersPath = path;
                            _t2H2nReader.InvalidateCache();
                            lock (_fishCacheLock) { _fishCache.Clear(); }
                            if (!string.IsNullOrEmpty(path))
                                System.Threading.Tasks.Task.Run(() => _t2H2nReader.PreloadAll());
                            SaveH2NSettingsT2();
                            T2Log($"[H2N] Путь ColorMarkers изменён: '{path}'");
                        }
                        break;

                    case "setT2FishMarkers":
                        {
                            string raw = root["value"]?.Value<string>() ?? "";
                            try { _t2H2nReader.MarkersFromJson(JArray.Parse(raw)); }
                            catch { _t2H2nReader.SetFishMarkerIds(raw.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries)); }
                            lock (_fishCacheLock) { _fishCache.Clear(); }
                            SaveH2NSettingsT2();
                            T2Log($"[H2N] Fish-маркеры: {_t2H2nReader.FishMarkers.Count} шт.");
                        }
                        break;

                    case "setT2RegPath":
                        {
                            string path = root["value"]?.Value<string>() ?? "";
                            _t2RegReader.ColorMarkersPath = path;
                            _t2RegReader.InvalidateCache();
                            lock (_regCacheLock) { _regCache.Clear(); }
                            if (!string.IsNullOrEmpty(path))
                                System.Threading.Tasks.Task.Run(() => _t2RegReader.PreloadAll());
                            SaveH2NSettingsT2();
                            T2Log($"[H2N] Reg path: '{path}'");
                        }
                        break;

                    case "setT2RegMarkers":
                        {
                            string raw = root["value"]?.Value<string>() ?? "";
                            try { _t2RegReader.MarkersFromJson(JArray.Parse(raw)); }
                            catch { _t2RegReader.SetFishMarkerIds(raw.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries)); }
                            lock (_regCacheLock) { _regCache.Clear(); }
                            SaveH2NSettingsT2();
                            T2Log($"[H2N] Reg-маркеры: {_t2RegReader.FishMarkers.Count} шт.");
                        }
                        break;

                    // ── Импорт маркеров из файла конфигурации трекера ──────────────────
                    case "importMarkersFromFile":
                    case "importT2FishMarkersFromFile":
                        this.BeginInvoke(new Action(() =>
                        {
                            string ver = _trackerVersion == TrackerVersion.H2N4 ? "h2nconfig" : "cg";
                            using (var dlg = new OpenFileDialog
                            {
                                Title = "Выберите файл маркеров Hand2Note",
                                Filter = _trackerVersion == TrackerVersion.H2N4
                                    ? "H2N4 Config|*.h2nconfig|Все|*.*"
                                    : "H2N3 ColorMarkers|*.cg|Все|*.*"
                            })
                            {
                                if (dlg.ShowDialog() != DialogResult.OK) return;
                                var entries = H2NColorNoteReader.ImportMarkersFromConfigFile(dlg.FileName);
                                if (entries.Count == 0)
                                {
                                    _ = PostToJS("setLog('Маркеры не найдены в файле', '#ef4444')");
                                    return;
                                }
                                _t2H2nReader.SetFishMarkers(entries);
                                lock (_fishCacheLock) { _fishCache.Clear(); }
                                _t2H2nReader.InvalidateCache();
                                SaveH2NSettingsT2();
                                _ = PostToJS($"setT2H2nState({JsonConvert.SerializeObject(_t2H2nReader.ColorMarkersPath)}, {_t2H2nReader.MarkersToJson()}, {JsonConvert.SerializeObject(_t2RegReader.ColorMarkersPath)}, {_t2RegReader.MarkersToJson()})");
                                _ = PostToJS($"setLog('Импортировано {entries.Count} маркеров', '#22c55e')");
                                T2Log($"[Import] Fish-маркеры из файла: {entries.Count} шт.");
                            }
                        }));
                        break;

                    case "importT2RegMarkersFromFile":
                        this.BeginInvoke(new Action(() =>
                        {
                            using (var dlg = new OpenFileDialog
                            {
                                Title = "Выберите файл маркеров Hand2Note (REG)",
                                Filter = _trackerVersion == TrackerVersion.H2N4
                                    ? "H2N4 Config|*.h2nconfig|Все|*.*"
                                    : "H2N3 ColorMarkers|*.cg|Все|*.*"
                            })
                            {
                                if (dlg.ShowDialog() != DialogResult.OK) return;
                                var entries = H2NColorNoteReader.ImportMarkersFromConfigFile(dlg.FileName);
                                if (entries.Count == 0)
                                {
                                    _ = PostToJS("setLog('Маркеры не найдены в файле', '#ef4444')");
                                    return;
                                }
                                _t2RegReader.SetFishMarkers(entries);
                                lock (_regCacheLock) { _regCache.Clear(); }
                                _t2RegReader.InvalidateCache();
                                SaveH2NSettingsT2();
                                _ = PostToJS($"setT2H2nState({JsonConvert.SerializeObject(_t2H2nReader.ColorMarkersPath)}, {_t2H2nReader.MarkersToJson()}, {JsonConvert.SerializeObject(_t2RegReader.ColorMarkersPath)}, {_t2RegReader.MarkersToJson()})");
                                _ = PostToJS($"setLog('Импортировано {entries.Count} reg-маркеров', '#22c55e')");
                                T2Log($"[Import] Reg-маркеры из файла: {entries.Count} шт.");
                            }
                        }));
                        break;

                    case "setT2SitOut":
                        {
                            _t2SitOutEnabled = root["enabled"]?.Value<bool>() ?? _t2SitOutEnabled;
                            _t2SitOutHands = root["hands"]?.Value<int>() ?? _t2SitOutHands;
                            _t2SitOutAutoMode = root["autoMode"]?.Value<bool>() ?? _t2SitOutAutoMode;
                            _t2SitOutSnoozeMin = root["snooze"]?.Value<int>() ?? _t2SitOutSnoozeMin;
                            if (!_t2SitOutEnabled)
                                lock (_t2SitOutLock) { _t2SitOutState.Clear(); }
                            SaveH2NSettingsT2();
                            T2Log($"[SitOut] enabled={_t2SitOutEnabled}, hands={_t2SitOutHands}, auto={_t2SitOutAutoMode}, snooze={_t2SitOutSnoozeMin}min");
                            _ = PostToJS($"setT2SitOutState({_t2SitOutEnabled.ToString().ToLower()},{_t2SitOutHands},{_t2SitOutAutoMode.ToString().ToLower()},{_t2SitOutSnoozeMin})");
                        }
                        break;

                    case "toggleAutoSeat":
                        this.BeginInvoke(new Action(() =>
                        {
                            _autoSeatEnabled = !_autoSeatEnabled;
                            if (_autoSeatEnabled)
                            {
                                _autoSeat.Start();
                                AppLog("РУМ", "AutoSeat: ВКЛЮЧЁН");
                            }
                            else
                            {
                                _autoSeat.Stop();
                                AppLog("РУМ", "AutoSeat: выключен");
                            }
                            _ = PostToJS($"setAutoSeatState({_autoSeatEnabled.ToString().ToLower()})");
                        }));
                        break;

                    case "setAutoSeatConfig":
                        this.BeginInvoke(new Action(() =>
                        {
                            _autoSeat.ScanIntervalMs = root["scanInterval"]?.Value<int>() ?? 500;
                            _autoSeat.DiffThreshold = root["diffThreshold"]?.Value<int>() ?? 40;
                            _autoSeat.ColorPct = root["colorPct"]?.Value<int>() ?? 20;
                            _autoSeat.CooldownMs = root["cooldown"]?.Value<int>() ?? 2500;

                            if (root["seatZones"] is Newtonsoft.Json.Linq.JArray zones && zones.Count == 6)
                            {
                                for (int si = 0; si < 6; si++)
                                {
                                    if (zones[si] is Newtonsoft.Json.Linq.JArray z && z.Count == 4)
                                        _autoSeat.SetSeatZone(si + 1,
                                            z[0].Value<double>(), z[1].Value<double>(),
                                            z[2].Value<double>(), z[3].Value<double>());
                                }
                            }
                            if (root["seatClicks"] is Newtonsoft.Json.Linq.JArray clicks && clicks.Count == 6)
                            {
                                for (int si = 0; si < 6; si++)
                                {
                                    if (clicks[si] is Newtonsoft.Json.Linq.JArray c && c.Count == 2)
                                        _autoSeat.SetSeatClick(si + 1,
                                            c[0].Value<double>(), c[1].Value<double>());
                                }
                            }
                            AppLog("РУМ", $"AutoSeat: настройки обновлены (interval={_autoSeat.ScanIntervalMs}мс)");
                        }));
                        break;

                    case "calibrateAutoSeat":
                        this.BeginInvoke(new Action(() =>
                        {
                            IntPtr calibHwnd = GetPrimaryTableHwnd();
                            if (calibHwnd == IntPtr.Zero)
                            {
                                AppLog("ОШИБКИ", "AutoSeat calibrate: стол не найден");
                                _ = PostToJS("setLog('AutoSeat: стол не найден!', '#ef4444')");
                                return;
                            }
                            string dbgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autoseat_debug.png");
                            AutoSeatCalibrator.SaveDebugImage(calibHwnd, dbgPath);
                            AppLog("РУМ", $"AutoSeat: debug-скриншот → {dbgPath}");
                            _ = PostToJS("setLog('AutoSeat: debug → autoseat_debug.png', '#22c55e')");
                        }));
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("WebMessage error: " + ex.Message);
            }
        }

        private void SyncActionsFromJS(JObject root)
        {
            try
            {
                var arr = root["actions"] as JArray;
                if (arr == null) return;

                foreach (JObject item in arr.OfType<JObject>())
                {
                    int id = item["id"].Value<int>();
                    var a = userActions.FirstOrDefault(x => x.InternalID == id);
                    if (a == null) continue;
                    if (!a.IsBase && item["name"] != null) a.DisplayName = item["name"].Value<string>();
                    if (item["betSize"] != null) a.SizeValue = item["betSize"].Value<string>();
                    if (item["useSize"] != null) a.UseSize = item["useSize"].Value<bool>();
                    if (item["enabled"] != null) a.IsEnabled = item["enabled"].Value<bool>();
                    if (item["key"] != null)
                    {
                        string keyStr = item["key"].Value<string>() ?? "";
                        if (keyStr == "—" || keyStr == "")
                        {
                            a.Key = Keys.None; a.UseCtrl = false; a.UseShift = false;
                        }
                        else
                        {
                            ParseKeyCombo(keyStr, out Keys parsedKey, out bool ctrl, out bool shift);
                            a.Key = parsedKey;
                            a.UseCtrl = ctrl;
                            a.UseShift = shift;
                        }
                    }
                }

                var ids = arr.OfType<JObject>()
                    .Select(item => item["id"]?.Value<int>() ?? -1)
                    .Where(id => id >= 0)
                    .ToList();
                var baseOld = userActions.Where(a => a.IsBase).ToList();
                var userOld = userActions.Where(a => !a.IsBase).ToList();
                var baseNew = ids.Select(id => baseOld.FirstOrDefault(a => a.InternalID == id)).Where(a => a != null).ToList();
                var userNew = ids.Select(id => userOld.FirstOrDefault(a => a.InternalID == id)).Where(a => a != null).ToList();
                foreach (var a in baseOld.Where(a => !baseNew.Contains(a))) baseNew.Add(a);
                foreach (var a in userOld.Where(a => !userNew.Contains(a))) userNew.Add(a);
                userActions.Clear();
                foreach (var a in baseNew) userActions.Add(a);
                foreach (var a in userNew) userActions.Add(a);

                SaveDataToFile(autoSavePath);
                SaveBaseState();
                SyncHotkeyEntriesToOverlay();
                overlay.DebugPoints.Clear();
            }
            catch (Exception ex)
            {
                AppLog("ОШИБКИ", $"SyncActionsFromJS: {ex.Message}");
            }
        }

        private void ParseKeyCombo(string combo, out Keys key, out bool ctrl, out bool shift)
            => KeyHelper.ParseKeyCombo(combo, out key, out ctrl, out shift);

        private int _bindingForId = -1;

        private void StartKeyBinding(int actionId)
        {
            _bindingForId = actionId;
            _ = PostToJS($"setKeyInputListening({actionId}, true)");
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
        }

        // ═══════════════════════════════════════════════════
        //  JS ↔ C# МОСТ
        // ═══════════════════════════════════════════════════
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.')
                    sb.Append(c);
            }
            string result = sb.ToString().Trim().TrimStart('.');
            if (result.Length > 64) result = result.Substring(0, 64);
            return result;
        }

        private Task PostToJS(string jsExpression)
        {
            try
            {
                if (webView?.CoreWebView2 == null) return System.Threading.Tasks.Task.CompletedTask;
                // ExecuteScriptAsync требует UI-поток — диспатчим через BeginInvoke
                if (InvokeRequired)
                {
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            if (webView?.CoreWebView2 != null)
                                await webView.CoreWebView2.ExecuteScriptAsync(jsExpression);
                        }
                        catch { }
                        finally { tcs.TrySetResult(true); }
                    }));
                    return tcs.Task;
                }
                else
                {
                    return webView.CoreWebView2.ExecuteScriptAsync(jsExpression)
                        .ContinueWith(_ => { });
                }
            }
            catch { return System.Threading.Tasks.Task.CompletedTask; }
        }

        private void PostToJSSafe(string jsExpression)
        {
            try
            {
                if (webView?.CoreWebView2 == null) return;
                if (InvokeRequired)
                {
                    Invoke(new Action(() =>
                    {
                        try
                        {
                            if (webView?.CoreWebView2 != null)
                                webView.CoreWebView2.ExecuteScriptAsync(jsExpression)
                                    .GetAwaiter().GetResult();
                        }
                        catch { }
                    }));
                }
                else
                {
                    _ = PostToJS(jsExpression);
                }
            }
            catch { }
        }

        private async Task SyncStateToUI()
        {
            AppLog("ОБЩЕЕ", "Синхронизация состояния с UI...");
            await SyncActionsToUI();
            await RefreshCfgListInUI();
            await RefreshLayoutListInUI();
            string statusJs = isRunning
                ? "setRunningState(true)"
                : "setRunningState(false)";
            await PostToJS(statusJs);
            await SendLicenseDaysToUI();
            SyncHotkeyEntriesToOverlay();
            var _showBindsLocal = overlay.ShowBinds;
            await PostToJS($"setShowBindsState({_showBindsLocal.ToString().ToLower()})");
            if (!string.IsNullOrEmpty(_converter.InputDir))
                await PostToJS($"converterSetInput({JsonConvert.SerializeObject(_converter.InputDir)})");
            if (!string.IsNullOrEmpty(_converter.OutputDir))
                await PostToJS($"converterSetOutput({JsonConvert.SerializeObject(_converter.OutputDir)})");
            // Sync H2N settings
            await PostToJS($"setH2nState({JsonConvert.SerializeObject(_h2nReader.ColorMarkersPath)}, {_h2nReader.MarkersToJson()}, {_autoSitOutEnabled.ToString().ToLower()})");
            // Sync Tables 2 H2N settings
            await PostToJS($"setT2H2nState({JsonConvert.SerializeObject(_t2H2nReader.ColorMarkersPath)}, {_t2H2nReader.MarkersToJson()}, {JsonConvert.SerializeObject(_t2RegReader.ColorMarkersPath)}, {_t2RegReader.MarkersToJson()})");
            await PostToJS($"setT2SitOutState({_t2SitOutEnabled.ToString().ToLower()},{_t2SitOutHands},{_t2SitOutAutoMode.ToString().ToLower()},{_t2SitOutSnoozeMin})");
            // Sync debug overlay state
            await PostToJS($"setDebugState({overlay.DebugMode.ToString().ToLower()})");
            // Sync table border state
            string borderColorHex = $"#{_tableBorderColor.R:X2}{_tableBorderColor.G:X2}{_tableBorderColor.B:X2}";
            await PostToJS($"setTableBorderState({_tableBorderEnabled.ToString().ToLower()}, '{borderColorHex.ToLower()}')");
            // Sync quick bet state
            await PostToJS($"setQuickBetState({JsonConvert.SerializeObject(_quickBetActionIds)})");
            // Sync autoStart state
            await PostToJS($"setAutoStartState({_autoStartConverter.ToString().ToLower()}, {_autoStartLayout.ToString().ToLower()}, {JsonConvert.SerializeObject(_autoStartLayoutName)})");
        }

        private async Task SyncActionsToUI()
        {
            var list = userActions.Select(a => new ActionDto
            {
                Id = a.InternalID,
                Name = a.DisplayName,
                Key = a.Key == Keys.None ? "—" : ((a.UseCtrl ? "Ctrl + " : "") + (a.UseShift ? "Shift + " : "") + KeyHelper.GetKeyDisplayName(a.Key)),
                BetSize = a.SizeValue,
                UseSize = a.UseSize,
                IsSet = a.IsSet,
                Enabled = a.IsEnabled,
                IsBase = a.IsBase,
                HideSize = a.HideSize
            });
            string json = JsonConvert.SerializeObject(list);
            await PostToJS($"loadActionsFromCS({json})");
        }

        private async Task RefreshCfgListInUI()
        {
            if (!Directory.Exists(configsFolder)) Directory.CreateDirectory(configsFolder);
            var names = Directory.GetFiles(configsFolder, "*.txt")
                .Select(f => Path.GetFileNameWithoutExtension(f)).ToArray();
            string json = JsonConvert.SerializeObject(names);
            await PostToJS($"setCfgList({json})");
        }

        private void SyncHotkeyEntriesToOverlay()
        {
            overlay.HotkeyEntries.Clear();
            foreach (var a in userActions)
            {
                string keyStr = a.Key == Keys.None ? "—" : ((a.UseCtrl ? "Ctrl+" : "") + (a.UseShift ? "Shift+" : "") + KeyHelper.GetKeyDisplayName(a.Key));
                overlay.HotkeyEntries.Add(new OverlayForm.HotkeyEntry
                {
                    Name = a.DisplayName,
                    Key = keyStr,
                    Enabled = a.IsSet || a.UseSize
                });
            }
            overlay.Invalidate();
        }

        private async Task RefreshLayoutListInUI(bool selectLast = false)
        {
            var names = layouts.Select(l => l.Name).ToArray();
            string json = JsonConvert.SerializeObject(names);
            string selectLastJs = selectLast ? "true" : "false";
            await PostToJS($"setLayoutList({json}, {selectLastJs})");
            if (activeLayout != null && activeLayout.Slots.Count > 0)
                await UpdateLayoutPreviewInUI(activeLayout);
        }

        private async Task UpdateLayoutPreviewInUI(LayoutConfig cfg)
        {
            if (cfg == null || cfg.Slots.Count == 0) return;
            var vscreen = SystemInformation.VirtualScreen;
            var slots = cfg.Slots.Select((s, i) => new SlotDto
            {
                X = s.Rect.X - vscreen.Left,
                Y = s.Rect.Y - vscreen.Top,
                W = s.Rect.Width,
                H = s.Rect.Height,
                Order = s.Order,
                SlotType = (int)s.Type,
                TypeIndex = s.TypeIndex
            }).ToList();

            var monitors = Screen.AllScreens.Select(sc => new MonitorDto
            {
                X = sc.Bounds.Left - vscreen.Left,
                Y = sc.Bounds.Top - vscreen.Top,
                W = sc.Bounds.Width,
                H = sc.Bounds.Height,
                Primary = sc.Primary,
                TaskbarH = sc.Bounds.Height - sc.WorkingArea.Height
            }).ToList();

            // ── Статусы открытых окон (active / idle) для превью ─────────────
            var openWindows = new JArray();
            try
            {
                var snapshot = GetTableWindowSnapshot();
                string inputDir = _converter?.InputDir ?? "";
                var statusMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrEmpty(inputDir) && Directory.Exists(inputDir))
                {
                    var now2 = DateTime.Now;
                    var byRaw2 = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f2 in Directory.GetFiles(inputDir, "*.txt"))
                    {
                        try
                        {
                            var fi2 = new FileInfo(f2);
                            var (_, raw2) = ParseTable2Name(Path.GetFileNameWithoutExtension(fi2.Name));
                            if (!byRaw2.ContainsKey(raw2) || fi2.LastWriteTime > byRaw2[raw2])
                                byRaw2[raw2] = fi2.LastWriteTime;
                        }
                        catch { }
                    }
                    foreach (var kv2 in byRaw2)
                    {
                        int age2 = (int)(now2 - kv2.Value).TotalSeconds;
                        // "active" = файл свежий (идёт игра), "idle" = просто открыто
                        statusMap[kv2.Key] = age2 < 90 ? "active" : "idle";
                    }
                }

                foreach (var entry in snapshot)
                {
                    string st = "idle";
                    foreach (var kv2 in statusMap)
                        if (entry.Title.IndexOf(kv2.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                        { st = kv2.Value; break; }
                    openWindows.Add(new JObject { ["title"] = entry.Title, ["status"] = st });
                }
            }
            catch { }

            string previewJson = JsonConvert.SerializeObject(new
            {
                screenW = vscreen.Width,
                screenH = vscreen.Height,
                slots,
                monitors,
                openWindows
            });
            await PostToJS($"setLayoutPreview({previewJson})");
        }

        // ═══════════════════════════════════════════════════
        //  ХУКИ И ЛОГИКА
        // ═══════════════════════════════════════════════════
        private IntPtr SetHook(LLKProc proc)
        {
            using (var p = Process.GetCurrentProcess()) using (var m = p.MainModule)
                return SetWindowsHookEx(13, proc, GetModuleHandle(m.ModuleName), 0);
        }

        private IntPtr HookCallback(int nc, IntPtr wp, IntPtr lp)
        {
            if (nc >= 0 && wp == (IntPtr)0x0100)
            {
                Keys key = (Keys)Marshal.ReadInt32(lp);
                bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
                bool shift = (GetKeyState(0x10) & 0x8000) != 0;

                if (_bindingForId >= 0)
                {
                    if (key != Keys.ControlKey && key != Keys.LControlKey && key != Keys.RControlKey &&
                        key != Keys.ShiftKey && key != Keys.LShiftKey && key != Keys.RShiftKey &&
                        key != Keys.Menu && key != Keys.LMenu && key != Keys.RMenu)
                    {
                        int bindId = _bindingForId;
                        _bindingForId = -1;
                        var a = userActions.FirstOrDefault(x => x.InternalID == bindId);
                        if (a != null)
                        {
                            a.Key = key;
                            a.UseCtrl = ctrl;
                            a.UseShift = shift;
                            string combo = (ctrl ? "Ctrl + " : "") + (shift ? "Shift + " : "") + KeyHelper.GetKeyDisplayName(key);
                            AppLog("ДЕЙСТВИЯ", $"[Хук] Клавиша задана: [{combo}] → {a.DisplayName}");
                            this.BeginInvoke(new Action(async () =>
                            {
                                await PostToJS($"setKeyLabel({bindId}, '{combo}')");
                                SaveDataToFile(autoSavePath);
                                SaveBaseState();
                                SyncHotkeyEntriesToOverlay();
                            }));
                        }
                    }
                    return CallNextHookEx(_hookID, nc, wp, lp);
                }

                if (isRunning)
                {
                    var act = userActions.FirstOrDefault(a => a.Key == key && a.UseCtrl == ctrl && a.UseShift == shift && a.IsEnabled);
                    if (act != null)
                    {
                        AppLog("ДЕЙСТВИЯ", $"Нажата клавиша [{(ctrl ? "Ctrl+" : "")}{(shift ? "Shift+" : "")}{KeyHelper.GetKeyDisplayName(key)}] → {act.DisplayName}");
                        Point snap = Cursor.Position;
                        Task.Run(() => ExecuteActionAt(act, snap));
                    }
                }
            }
            return CallNextHookEx(_hookID, nc, wp, lp);
        }

        private void ExecuteActionAt(DynamicAction action, Point cursorPos)
        {
            IntPtr hwnd = GetWindowAtPoint(cursorPos);
            if (hwnd == IntPtr.Zero) { AppLog("РЕАКЦИИ", $"[{action.DisplayName}] окно не найдено"); return; }

            RECT r = GetClientBounds(hwnd);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;

            var sb = new System.Text.StringBuilder(256); GetWindowText(hwnd, sb, 256);
            AppLog("РЕАКЦИИ", $"[{action.DisplayName}] окно: {sb} [{w}x{h}] client origin ({r.Left},{r.Top})");

            if (action.UseSize && isGlobalInputSet)
            {
                int cx = (int)(w * globalInputX), cy = (int)(h * globalInputY);
                AppLog("РЕАКЦИИ", $"[{action.DisplayName}] вставка ставки {action.SizeValue} в ({(int)(globalInputX * 100)}%,{(int)(globalInputY * 100)}%)");
                var t = new Thread(() => PasteValue(hwnd, cx, cy, action.SizeValue));
                t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(4000);
            }
            else if (!action.UseSize && action.IsSet)
            {
                int cx = (int)(w * action.RelX), cy = (int)(h * action.RelY);
                if (_quickBetActionIds.Contains(action.InternalID))
                {
                    // ── Быстрая ставка: клик по таргету, затем клик по Raise ──
                    AppLog("РЕАКЦИИ", $"[{action.DisplayName}] быстрая ставка: клик ({(int)(action.RelX * 100)}%,{(int)(action.RelY * 100)}%) → Raise");
                    SilentClick(hwnd, cx, cy);
                    Thread.Sleep(80);
                    var raiseAction = userActions.FirstOrDefault(a =>
                        a.IsBase &&
                        string.Equals(a.DisplayName, "Raise", StringComparison.OrdinalIgnoreCase) &&
                        a.IsSet);
                    if (raiseAction != null)
                        SilentClick(hwnd, (int)(w * raiseAction.RelX), (int)(h * raiseAction.RelY));
                }
                else
                {
                    AppLog("РЕАКЦИИ", $"[{action.DisplayName}] клик ({(int)(action.RelX * 100)}%,{(int)(action.RelY * 100)}%) → client ({cx},{cy})");
                    SilentClick(hwnd, cx, cy);
                }
            }
        }

        private IntPtr GetWindowAtPoint(Point p)
        {
            IntPtr res = IntPtr.Zero;
            EnumWindows((hWnd, lp) => {
                var sb = new StringBuilder(256); GetWindowText(hWnd, sb, 256); string t = sb.ToString();
                if ((IsTableWindow(t) || t.Contains("ТЕСТОВАЯ ЗОНА")) && !t.Contains("1WIN TOOLS"))
                {
                    RECT r = GetClientBounds(hWnd);
                    if (p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom) { res = hWnd; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return res;
        }

        private IntPtr GetWindowUnderCursor() => GetWindowAtPoint(Cursor.Position);

        private void TogglePower()
        {
            isRunning = !isRunning;
            if (isRunning)
            {
                SaveDataToFile(autoSavePath);
                _hookID = SetHook(_hookProc);
                AppLog("ДЕЙСТВИЯ", "Хук включён — режим активен");

                // ── AutoStart: применяем функции при старте ──
                if (_autoStartConverter)
                {
                    if (string.IsNullOrEmpty(_converter.InputDir) || string.IsNullOrEmpty(_converter.OutputDir))
                    {
                        AppLog("ДЕЙСТВИЯ", "AutoStart: конвертор не запущен — не выбраны папки Input/Output");
                    }
                    else
                    {
                        _converter.LoadCache();
                        _converter.StartLive(msg =>
                        {
                            _ = PostToJS($"converterLog({JsonConvert.SerializeObject(msg)}, '#22c55e')");
                            AppLog("РУМ", $"[Converter] {msg}");
                        });
                        _ = PostToJS("converterOnStarted()");
                        AppLog("ДЕЙСТВИЯ", "AutoStart: конвертор LIVE запущен");
                    }
                }
                if (_autoStartLayout)
                {
                    var targetLayout = !string.IsNullOrEmpty(_autoStartLayoutName)
                        ? layouts.FirstOrDefault(l => l.Name == _autoStartLayoutName)
                        : null;

                    if (targetLayout != null)
                    {
                        activeLayout = targetLayout;
                        _hwndSlotMap.Clear();
                        _hwndMovedAt.Clear();
                        _hwndTargetRect.Clear();
                        _autoLayoutEnabled = true;
                        _autoLayoutTimer.Start();
                        _ = PostToJS($"setAutoLayoutState(true)");
                        _ = UpdateLayoutPreviewInUI(activeLayout);
                        AppLog("ДЕЙСТВИЯ", $"AutoStart: постоянная расстановка включена — лейаут: {_autoStartLayoutName}");
                    }
                    else
                    {
                        AppLog("ДЕЙСТВИЯ", $"AutoStart: лейаут '{_autoStartLayoutName}' не найден — расстановка не включена");
                    }
                }
            }
            else
            {
                if (_hookID != IntPtr.Zero) { UnhookWindowsHookEx(_hookID); _hookID = IntPtr.Zero; AppLog("ДЕЙСТВИЯ", "Хук выключен"); }
            }
            overlay.IsActive = isRunning;
            _ = PostToJS($"setRunningState({isRunning.ToString().ToLower()})");
        }

        // ── Глобальный хук мыши для capture по клику ──────
        private IntPtr _mouseHookID = IntPtr.Zero;
        private LLMProc _mouseHookProcRef;
        private delegate IntPtr LLMProc(int nc, IntPtr wp, IntPtr lp);
        private int _captureMode = 0;
        private int _captureActionId = -1;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

        private void StartMouseCapture(int mode, int actionId = -1)
        {
            if (_mouseHookID != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHookID); _mouseHookID = IntPtr.Zero; }
            _captureMode = mode;
            _captureActionId = actionId;
            _mouseHookProcRef = MouseHookCallback;
            using (var p = Process.GetCurrentProcess()) using (var m = p.MainModule)
                _mouseHookID = SetWindowsHookEx(14, _mouseHookProcRef, GetModuleHandle(m.ModuleName), 0);
        }

        private void StopMouseCapture()
        {
            if (_mouseHookID != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHookID); _mouseHookID = IntPtr.Zero; }
            _captureMode = 0; _captureActionId = -1;
        }

        private IntPtr MouseHookCallback(int nc, IntPtr wp, IntPtr lp)
        {
            const int WM_LBUTTONDOWN_M = 0x0201;
            if (nc >= 0 && wp == (IntPtr)WM_LBUTTONDOWN_M && _captureMode != 0)
            {
                var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lp);
                int cx = ms.pt.X, cy = ms.pt.Y;
                int mode = _captureMode; int actId = _captureActionId;
                StopMouseCapture();

                IntPtr hWnd = WindowFromPoint(new POINT { X = cx, Y = cy });
                if (hWnd != IntPtr.Zero)
                {
                    RECT rect = GetClientBounds(hWnd);
                    int cw = rect.Right - rect.Left;
                    int ch = rect.Bottom - rect.Top;

                    if (cw > 0 && ch > 0)
                    {
                        double rx = Math.Max(0, Math.Min(1, (double)(cx - rect.Left) / cw));
                        double ry = Math.Max(0, Math.Min(1, (double)(cy - rect.Top) / ch));

                        this.BeginInvoke(new Action(async () =>
                        {
                            if (mode == 1)
                            {
                                globalInputX = rx; globalInputY = ry;
                                isGlobalInputSet = true; SaveDataToFile(autoSavePath);
                                AppLog("ДЕЙСТВИЯ", $"INPUT установлен: ({(int)(rx * 100)}%, {(int)(ry * 100)}%)");
                                await PostToJS("setLog('\u2714 INPUT SET', '#22c55e')");
                            }
                            else if (mode == 2)
                            {
                                var a = userActions.FirstOrDefault(x => x.InternalID == actId);
                                if (a != null)
                                {
                                    a.RelX = rx; a.RelY = ry;
                                    a.IsSet = true; SaveDataToFile(autoSavePath);
                                    AppLog("ДЕЙСТВИЯ", $"TARGET [{a.DisplayName}] установлен: ({(int)(rx * 100)}%, {(int)(ry * 100)}%)");
                                    await PostToJS($"setActionSet({actId}, true)");
                                    await PostToJS("setLog('\u2714 TARGET SET', '#22c55e')");
                                }
                            }
                        }));
                    }
                }
                else
                {
                    this.BeginInvoke(new Action(async () =>
                        await PostToJS("setLog('\u041e\u043a\u043d\u043e \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d\u043e', '#ef4444')")));
                }

                return (IntPtr)1;
            }
            return CallNextHookEx(_mouseHookID, nc, wp, lp);
        }

        private async Task CaptureGlobalInput()
        {
            await PostToJS("setLog('\u041a\u043b\u0438\u043a\u043d\u0438\u0442\u0435 \u043f\u043e \u043f\u043e\u043b\u044e \u0432\u0432\u043e\u0434\u0430 \u043d\u0430 \u0441\u0442\u043e\u043b\u0435...', '#fbbf24')");
            this.BeginInvoke(new Action(() => StartMouseCapture(1)));
        }

        private async Task StartLearning(int actionId)
        {
            var la = userActions.FirstOrDefault(x => x.InternalID == actionId);
            string ln = la?.DisplayName ?? "?";
            await PostToJS($"setLog('КЛИКНИТЕ ПО КНОПКЕ \"{ln}\" НА СТОЛЕ... нажмите Esc, чтобы отменить', '#fbbf24')");
            this.BeginInvoke(new Action(() => StartMouseCapture(2, actionId)));
        }

        private void OpenLayoutEditor()
        {
            int measuredTitleH = 0;
            float measuredAR = 0f;
            EnumWindows((hWnd, lp) => {
                var sb2 = new StringBuilder(256); GetWindowText(hWnd, sb2, 256);
                string t2 = sb2.ToString();
                if (IsTableWindow(t2) && !t2.Contains("1WIN TOOLS"))
                {
                    GetWindowRect(hWnd, out RECT wr2);
                    RECT cb2 = GetClientBounds(hWnd);
                    int th2 = cb2.Top - wr2.Top;
                    int cw2 = cb2.Right - cb2.Left;
                    int ch2 = cb2.Bottom - cb2.Top;
                    if (th2 > 0 && th2 < 100 && cw2 > 100 && ch2 > 100)
                    {
                        measuredTitleH = th2;
                        measuredAR = (float)cw2 / ch2;
                        AppLog("ДЕЙСТВИЯ", $"Редактор: titlebar={th2}px  client={cw2}×{ch2}  AR={measuredAR:F3}");
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            using (var ed = new LayoutEditorForm(activeLayout, measuredTitleH, measuredAR))
            {
                if (ed.ShowDialog() == DialogResult.OK)
                {
                    var nc = ed.ResultConfig;
                    var ex = layouts.FirstOrDefault(l => l.Name == nc.Name);
                    if (ex != null) layouts.Remove(ex);
                    layouts.Add(nc); activeLayout = nc;
                    SaveLayoutsToFile();
                    AppLog("ДЕЙСТВИЯ", $"Сохранён лейаут: {nc.Name}");
                    _ = RefreshLayoutListInUI(selectLast: true);
                }
            }
        }

        private void SaveCurrentWindowLayout(string name)
        {
            var wins = new List<IntPtr>();
            EnumWindows((hWnd, lp) => {
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                string t = sb.ToString();
                if (IsTableWindow(t))
                    wins.Add(hWnd);
                return true;
            }, IntPtr.Zero);

            if (wins.Count == 0)
            {
                _ = PostToJS("setLog('Нет открытых столов 1win!', '#ef4444')");
                AppLog("ОШИБКИ", "SaveCurrentLayout: столы не найдены");
                return;
            }

            var entries = wins.Select(h => {
                GetWindowRect(h, out RECT wr);
                RECT cb = GetClientBounds(h);
                var rawRect = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
                int vcx = (cb.Left + cb.Right) / 2;
                int vcy = (cb.Top + cb.Bottom) / 2;
                return new { rawRect, vcx, vcy };
            }).ToList();

            const int ROW_TOLERANCE = 80;
            var byCenterY = entries.OrderBy(e => e.vcy).ThenBy(e => e.vcx).ToList();

            var rowAssign = new int[byCenterY.Count];
            int curRow = 0;
            int rowAnchorY = byCenterY[0].vcy;
            for (int i = 0; i < byCenterY.Count; i++)
            {
                if (byCenterY[i].vcy - rowAnchorY > ROW_TOLERANCE)
                {
                    curRow++;
                    rowAnchorY = byCenterY[i].vcy;
                }
                rowAssign[i] = curRow;
            }

            var finalSorted = byCenterY
                .Select((e, i) => new { e, row = rowAssign[i] })
                .OrderBy(x => x.row).ThenBy(x => x.e.vcx)
                .Select(x => x.e.rawRect)
                .ToList();

            var newLayout = new LayoutConfig { Name = name };
            for (int i = 0; i < finalSorted.Count; i++)
            {
                var rc = finalSorted[i];
                newLayout.Slots.Add(new TableSlot { Rect = rc, Order = i + 1 });
                AppLog("ДЕЙСТВИЯ", $"  Слот #{i + 1}: WindowRect ({rc.X},{rc.Y}) {rc.Width}×{rc.Height}");
            }

            var existing = layouts.FirstOrDefault(l => l.Name == name);
            if (existing != null) layouts.Remove(existing);
            layouts.Add(newLayout);
            activeLayout = newLayout;

            SaveLayoutsToFile();
            AppLog("ДЕЙСТВИЯ", $"Сохранена текущая расстановка: \"{name}\" ({wins.Count} столов)");
            _ = PostToJS($"setLog({JsonConvert.SerializeObject($"Расстановка сохранена: {name} ({wins.Count} ст.")}, '#22c55e')");
            _ = RefreshLayoutListInUI(selectLast: true);
        }

        private void OpenTestZone()
        {
            string imgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "t.jpg");
            Bitmap bmp = null;
            if (File.Exists(imgPath)) try { bmp = (Bitmap)Image.FromFile(imgPath); } catch { }
            new TestZoneForm(bmp,
                () => userActions,
                () => globalInputX,
                () => globalInputY,
                () => isGlobalInputSet).Show();
        }

        private void ApplyLayoutToWindows()
        {
            if (activeLayout == null || activeLayout.Slots.Count == 0) return;

            var wins = new List<IntPtr>();
            EnumWindows((hWnd, lp) => {
                var sb = new StringBuilder(256); GetWindowText(hWnd, sb, 256);
                string t = sb.ToString();
                if (IsTableWindow(t)) wins.Add(hWnd);
                return true;
            }, IntPtr.Zero);

            var pairs = new List<(IntPtr hwnd, TableSlot slot)>();
            for (int i = 0; i < Math.Min(wins.Count, activeLayout.Slots.Count); i++)
                pairs.Add((wins[i], activeLayout.Slots[i]));

            Task.Run(() => {
                foreach (var (hwnd, sl) in pairs)
                    MoveWindowToSlotRect(hwnd, sl.Rect);

                System.Threading.Thread.Sleep(200);

                foreach (var (hwnd, sl) in pairs)
                {
                    MoveWindowToSlotRect(hwnd, sl.Rect);
                    this.BeginInvoke(new Action(() =>
                        AppLog("РУМ", $"Стол → ({sl.Rect.X},{sl.Rect.Y}) [{sl.Rect.Width}×{sl.Rect.Height}]")));
                }
            });
        }

        private void AutoLayoutTick()
        {
            if (activeLayout == null || activeLayout.Slots.Count == 0) return;

            // ── Берём снимок из TableManager (уже обновлён _masterScanTimer) ──
            var snapshot = GetTableWindowSnapshot();
            var wins = snapshot.Select(e => e.Hwnd).ToList();

            int slotCount = activeLayout.Slots.Count;
            if (wins.Count == 0) { _hwndSlotMap.Clear(); _hwndMovedAt.Clear(); _hwndTargetRect.Clear(); return; }

            // ── Определяем приоритет окна через TableManager + _lastTableInfos ─
            // Активный (Active): стол в фокусе ИЛИ есть фиш
            // Неактивный (Inactive): минимизирован ИЛИ нет HH-активности
            // Default: всё остальное
            var fishyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var snap = _lastTableInfos; // volatile — безопасно читать без lock
                foreach (var t in snap)
                {
                    if (t.Fishy) fishyNames.Add(t.Name);
                    if (t.Active) liveNames.Add(t.Name);
                }
            }
            catch { }

            SlotType GetWindowPriority(TableWindowEntry entry)
            {
                // Стол в фокусе → Active (мгновенный отклик без IO)
                if (entry.Focused) return SlotType.Active;
                // Фиш за столом → Active
                foreach (var fn in fishyNames)
                    if (entry.Title.IndexOf(fn, StringComparison.OrdinalIgnoreCase) >= 0)
                        return SlotType.Active;
                // Минимизирован → Inactive
                if (entry.Minimized) return SlotType.Inactive;
                // Нет HH-активности → Inactive
                // Это включает оба случая:
                //   1. Стол открыт, но ни одной раздачи не сыграно (isOpenIdle)
                //   2. Стол открыт, но раздачи давно (> 90 сек)
                bool hasHHActivity = false;
                foreach (var ln in liveNames)
                    if (entry.Title.IndexOf(ln, StringComparison.OrdinalIgnoreCase) >= 0)
                    { hasHHActivity = true; break; }
                if (!hasHHActivity) return SlotType.Inactive;
                return SlotType.Default;
            }

            // Словарь hwnd→entry для быстрого доступа
            var entryMap = snapshot.ToDictionary(e => e.Hwnd);

            // ── 1. Удаляем закрытые окна ──────────────────────────────────────
            var winSet = new HashSet<IntPtr>(wins);
            var closedHwnds = _hwndSlotMap.Keys.Where(h => !winSet.Contains(h)).ToList();
            var freedSlotNums = new List<int>();
            foreach (var dead in closedHwnds)
            {
                if (_hwndSlotMap.TryGetValue(dead, out int fs)) freedSlotNums.Add(fs);
                _hwndSlotMap.Remove(dead); _hwndMovedAt.Remove(dead); _hwndTargetRect.Remove(dead);
            }

            // ── 2. Компактная упаковка при освобождении слота ─────────────────
            if (freedSlotNums.Count > 0)
            {
                freedSlotNums.Sort();
                foreach (int freeSlotNum in freedSlotNums)
                {
                    if (freeSlotNum < 1 || freeSlotNum > slotCount) continue;
                    var freeSlotDef = activeLayout.Slots[freeSlotNum - 1];
                    var candidate = _hwndSlotMap
                        .Where(kv => {
                            int si = kv.Value - 1;
                            return kv.Value > freeSlotNum && si >= 0 && si < slotCount
                                && activeLayout.Slots[si].Type == freeSlotDef.Type;
                        })
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => (IntPtr?)kv.Key)
                        .FirstOrDefault();
                    if (candidate.HasValue && candidate.Value != IntPtr.Zero)
                    {
                        _hwndSlotMap[candidate.Value] = freeSlotNum;
                        _hwndTargetRect.Remove(candidate.Value);
                        _hwndMovedAt.Remove(candidate.Value);
                        AppLog("РУМ", $"[Auto] Стол переезжает на слот #{freeSlotNum} [{freeSlotDef.Type}]");
                    }
                }
            }

            // ── 3. Проверяем смену приоритета для уже назначенных окон ─────────
            // Если стол получил фокус (Active) но сидит в Default/Inactive слоте —
            // ищем свободный Active-слот и перебрасываем его.
            foreach (var kv in _hwndSlotMap.ToList())
            {
                IntPtr hwnd = kv.Key;
                if (!entryMap.TryGetValue(hwnd, out var entry)) continue;
                int currentSlotIdx = kv.Value - 1;
                if (currentSlotIdx < 0 || currentSlotIdx >= slotCount) continue;

                SlotType currentSlotType = activeLayout.Slots[currentSlotIdx].Type;
                SlotType windowPrio = GetWindowPriority(entry);

                // Стол стал Active, но сидит в Inactive/Default слоте → переброс
                if (windowPrio == SlotType.Active && currentSlotType != SlotType.Active)
                {
                    var usedNow = new HashSet<int>(_hwndSlotMap.Values);
                    for (int i = 0; i < slotCount; i++)
                    {
                        if (activeLayout.Slots[i].Type == SlotType.Active && !usedNow.Contains(i + 1))
                        {
                            int oldSlot = kv.Value;
                            _hwndSlotMap[hwnd] = i + 1;
                            _hwndTargetRect.Remove(hwnd);
                            _hwndMovedAt.Remove(hwnd);
                            AppLog("РУМ", $"[Auto] Фокус/фиш: стол перебрасывается с #{oldSlot} → Active слот #{i + 1}");
                            break;
                        }
                    }
                }
                // Стол стал Inactive (минимизирован) но в Active слоте → освобождаем
                else if (windowPrio == SlotType.Inactive && currentSlotType == SlotType.Active)
                {
                    var usedNow = new HashSet<int>(_hwndSlotMap.Values);
                    for (int i = 0; i < slotCount; i++)
                    {
                        if (activeLayout.Slots[i].Type == SlotType.Inactive && !usedNow.Contains(i + 1))
                        {
                            int oldSlot = kv.Value;
                            _hwndSlotMap[hwnd] = i + 1;
                            _hwndTargetRect.Remove(hwnd);
                            _hwndMovedAt.Remove(hwnd);
                            AppLog("РУМ", $"[Auto] Минимизирован: стол освобождает #{oldSlot} → Inactive слот #{i + 1}");
                            break;
                        }
                    }
                }
            }

            // ── 4. Назначаем слоты новым окнам (с учётом типа) ───────────────
            var newWins = wins.Where(h => !_hwndSlotMap.ContainsKey(h)).ToList();
            if (newWins.Count > 0)
            {
                var usedSlots = new HashSet<int>(_hwndSlotMap.Values);
                var slotsByType = activeLayout.Slots
                    .Select((sl, idx) => (sl, num: idx + 1))
                    .OrderBy(x => x.sl.TypeIndex)
                    .GroupBy(x => x.sl.Type)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.num).ToList());

                int FindFreeSlot(params SlotType[] preference)
                {
                    foreach (var pref in preference)
                    {
                        if (!slotsByType.TryGetValue(pref, out var candidates)) continue;
                        foreach (var num in candidates)
                            if (!usedSlots.Contains(num)) return num;
                    }
                    return -1;
                }

                foreach (var hw in newWins)
                {
                    if (!entryMap.TryGetValue(hw, out var entry)) continue;
                    SlotType prio = GetWindowPriority(entry);
                    int assignedSlot;
                    if (prio == SlotType.Active)
                        assignedSlot = FindFreeSlot(SlotType.Active, SlotType.Default);
                    else if (prio == SlotType.Inactive)
                        assignedSlot = FindFreeSlot(SlotType.Inactive, SlotType.Default);
                    else
                        assignedSlot = FindFreeSlot(SlotType.Default, SlotType.Active, SlotType.Inactive);

                    if (assignedSlot > 0)
                    {
                        _hwndSlotMap[hw] = assignedSlot;
                        _hwndTargetRect.Remove(hw);
                        usedSlots.Add(assignedSlot);
                        AppLog("РУМ", $"[Auto] Новый стол [{prio}] → слот #{assignedSlot}");
                    }
                }
            }

            // ── 5. Ставим окна на целевые позиции ────────────────────────────
            const int TOLERANCE = 14;
            foreach (var kv in _hwndSlotMap.ToList())
            {
                IntPtr hwnd = kv.Key;
                int slotIdx = kv.Value - 1;
                if (slotIdx < 0 || slotIdx >= slotCount) continue;

                var sl = activeLayout.Slots[slotIdx];

                if (_hwndMovedAt.TryGetValue(hwnd, out DateTime lastMoved)
                    && (DateTime.Now - lastMoved) < AutoLayoutCooldown)
                    continue;

                GetWindowRect(hwnd, out RECT curWR);
                bool ok;
                if (_hwndTargetRect.TryGetValue(hwnd, out RECT targetWR))
                {
                    ok = Math.Abs(curWR.Left - targetWR.Left) <= TOLERANCE
                      && Math.Abs(curWR.Top - targetWR.Top) <= TOLERANCE
                      && Math.Abs((curWR.Right - curWR.Left) - (targetWR.Right - targetWR.Left)) <= TOLERANCE
                      && Math.Abs((curWR.Bottom - curWR.Top) - (targetWR.Bottom - targetWR.Top)) <= TOLERANCE;
                }
                else
                {
                    RECT cb2 = GetClientBounds(hwnd);
                    GetWindowRect(hwnd, out RECT wr2);
                    int realTitleH = cb2.Top - wr2.Top;
                    if (realTitleH < 0 || realTitleH > 100) realTitleH = WIN_TITLE_H_APPROX;
                    ok = Math.Abs(cb2.Left - sl.Rect.X) <= TOLERANCE
                      && Math.Abs(cb2.Top - (sl.Rect.Y + realTitleH)) <= TOLERANCE
                      && Math.Abs((cb2.Right - cb2.Left) - sl.Rect.Width) <= TOLERANCE
                      && Math.Abs((cb2.Bottom - cb2.Top) - (sl.Rect.Height - realTitleH)) <= TOLERANCE;
                }

                if (!ok)
                {
                    MoveWindowToSlotRect(hwnd, sl.Rect);
                    GetWindowRect(hwnd, out RECT newWR);
                    _hwndTargetRect[hwnd] = newWR;
                    _hwndMovedAt[hwnd] = DateTime.Now;
                    AppLog("РУМ", $"[Auto] Стол [{sl.Type}] → слот #{kv.Value}  ({newWR.Left},{newWR.Top})");
                }
            }
        }


        private void SnapAndSwapWindow(IntPtr movedHwnd)
        {
            if (activeLayout == null || activeLayout.Slots.Count == 0) return;

            RECT cb = GetClientBounds(movedHwnd);
            int cx = (cb.Left + cb.Right) / 2;
            int cy = (cb.Top + cb.Bottom) / 2;

            int bestSlot = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < activeLayout.Slots.Count; i++)
            {
                var s = activeLayout.Slots[i];
                int scx = s.Rect.X + s.Rect.Width / 2;
                int scy = s.Rect.Y + s.Rect.Height / 2;
                double d = Math.Sqrt((cx - scx) * (cx - scx) + (cy - scy) * (cy - scy));

                double thresh = Math.Max(s.Rect.Width, s.Rect.Height) * 0.40;
                if (d < thresh && d < bestDist) { bestDist = d; bestSlot = i; }
            }
            if (bestSlot < 0) return;

            var targetSlot = activeLayout.Slots[bestSlot];

            if (swapEnabled)
            {
                var allWins = new List<IntPtr>();
                EnumWindows((hWnd, lp) => {
                    if (hWnd == movedHwnd) return true;
                    var sb = new StringBuilder(256); GetWindowText(hWnd, sb, 256);
                    if (IsTableWindow(sb.ToString())) allWins.Add(hWnd);
                    return true;
                }, IntPtr.Zero);

                foreach (var other in allWins)
                {
                    RECT ocb = GetClientBounds(other);
                    int ocx = (ocb.Left + ocb.Right) / 2;
                    int ocy = (ocb.Top + ocb.Bottom) / 2;
                    if (ocx >= targetSlot.Rect.Left && ocx <= targetSlot.Rect.Right &&
                        ocy >= targetSlot.Rect.Top && ocy <= targetSlot.Rect.Bottom)
                    {
                        PlaceWindowAtClientPos(other, cb.Left, cb.Top,
                            cb.Right - cb.Left, cb.Bottom - cb.Top);
                        AppLog("РУМ", $"Swap: поменяли окна местами (слот #{bestSlot + 1})");
                        break;
                    }
                }
            }

            if (snapEnabled)
            {
                int tw = _tableW > 0 ? _tableW : targetSlot.Rect.Width;
                int th = _tableH > 0 ? _tableH : targetSlot.Rect.Height;
                PlaceWindowAtClientPos(movedHwnd, targetSlot.Rect.X, targetSlot.Rect.Y, tw, th);
                AppLog("РУМ", $"Snap: окно → слот #{bestSlot + 1} ({targetSlot.Rect.X},{targetSlot.Rect.Y})");
            }
        }

        private void PlaceWindowAtClientPos(IntPtr hwnd, int cx, int cy, int cw, int ch)
        {
            GetWindowRect(hwnd, out RECT wr);
            RECT cb = GetClientBounds(hwnd);
            int offX = cb.Left - wr.Left;
            int offY = cb.Top - wr.Top;
            int offR = wr.Right - cb.Right;
            int offB = wr.Bottom - cb.Bottom;
            MoveWindow(hwnd, cx - offX, cy - offY, cw + offX + offR, ch + offY + offB, true);
        }

        private void MoveWindowToSlotRect(IntPtr hwnd, Rectangle slotRect)
        {
            GetWindowRect(hwnd, out RECT wr);
            RECT cb = GetClientBounds(hwnd);

            int offX = cb.Left - wr.Left;
            int offR = wr.Right - cb.Right;
            int offB = wr.Bottom - cb.Bottom;

            int wx = slotRect.X - offX;
            int wy = slotRect.Y;
            int ww = slotRect.Width + offX + offR;
            int wh = slotRect.Height + offB;

            MoveWindow(hwnd, wx, wy, ww, wh, true);
        }

        private void StartTableDetect()
        {
            _detectStep = 1;
            _ = PostToJS("setDetectStatus('⏳ Кликни по верхнему-левому углу стола...')");
            AppLog("ДЕЙСТВИЯ", "Детектор: ждём угол 1");
            _mouseHookProcRef = DetectMouseHook;
            using (var p = Process.GetCurrentProcess()) using (var m = p.MainModule)
                _mouseHookID = SetWindowsHookEx(14, _mouseHookProcRef, GetModuleHandle(m.ModuleName), 0);
        }

        private IntPtr DetectMouseHook(int nc, IntPtr wp, IntPtr lp)
        {
            const int WM_LBUTTONDOWN_D = 0x0201;
            if (nc >= 0 && wp == (IntPtr)WM_LBUTTONDOWN_D && _detectStep > 0)
            {
                var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lp);
                if (_detectStep == 1)
                {
                    _detectX1 = ms.pt.X; _detectY1 = ms.pt.Y;
                    _detectStep = 2;
                    this.BeginInvoke(new Action(async () => {
                        await PostToJS("setDetectStatus('⏳ Теперь кликни по нижнему-правому углу...')");
                        AppLog("ДЕЙСТВИЯ", $"Детектор: угол 1 = ({_detectX1},{_detectY1})");
                    }));
                    return (IntPtr)1;
                }
                else if (_detectStep == 2)
                {
                    int x2 = ms.pt.X, y2 = ms.pt.Y;
                    _tableW = Math.Abs(x2 - _detectX1);
                    _tableH = Math.Abs(y2 - _detectY1);
                    _detectStep = 0;

                    if (_mouseHookID != IntPtr.Zero)
                    { UnhookWindowsHookEx(_mouseHookID); _mouseHookID = IntPtr.Zero; }

                    try
                    {
                        File.WriteAllLines(tableSizePath, new[] {
                            $"width={_tableW}",
                            $"height={_tableH}",
                            $"detected={DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                        });
                    }
                    catch { }

                    this.BeginInvoke(new Action(async () => {
                        string msg = $"✔ Размер: {_tableW}×{_tableH} px (сохранено в table_size.txt)";
                        await PostToJS($"setDetectStatus('{msg}')");
                        AppLog("ДЕЙСТВИЯ", $"Детектор: размер стола {_tableW}×{_tableH}");
                    }));

                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_mouseHookID, nc, wp, lp);
        }

        private readonly Dictionary<IntPtr, Point> _stableWinPos = new Dictionary<IntPtr, Point>();
        private readonly Dictionary<IntPtr, int> _stableCounter = new Dictionary<IntPtr, int>();
        private const int STABLE_TICKS = 2;

        private void CheckSnapWindows()
        {
            if (!snapEnabled && !swapEnabled) return;
            if (activeLayout == null || activeLayout.Slots.Count == 0) return;

            // Берём уже готовый снимок из TableManager (без повторного EnumWindows)
            var snapshot = GetTableWindowSnapshot();
            var wins = snapshot.Select(e => e.Hwnd).ToList();

            var seen = new HashSet<IntPtr>(wins);

            foreach (var k in _stableWinPos.Keys.ToList())
                if (!seen.Contains(k)) { _stableWinPos.Remove(k); _stableCounter.Remove(k); }

            foreach (var hwnd in wins)
            {
                GetWindowRect(hwnd, out RECT wr);
                var pos = new Point(wr.Left, wr.Top);

                if (!_stableWinPos.ContainsKey(hwnd))
                {
                    _stableWinPos[hwnd] = pos;
                    _stableCounter[hwnd] = 0;
                    continue;
                }

                if (_stableWinPos[hwnd] == pos)
                {
                    _stableCounter[hwnd]++;
                    if (_stableCounter[hwnd] == STABLE_TICKS)
                    {
                        SnapAndSwapWindow(hwnd);
                        _stableCounter[hwnd] = STABLE_TICKS + 1;
                    }
                }
                else
                {
                    _stableWinPos[hwnd] = pos;
                    _stableCounter[hwnd] = 0;
                }
            }
        }

        // Предыдущее состояние overlay для dirty-check
        private IntPtr _prevOverlayHwnd = IntPtr.Zero;
        private Rectangle _prevOverlayBounds = Rectangle.Empty;
        private bool _prevOverlayVisible = false;

        private bool UpdateOverlay()
        {
            IntPtr hWnd = GetWindowUnderCursor();
            bool wantVisible = (hWnd != IntPtr.Zero && isRunning);

            if (!wantVisible)
            {
                if (!_prevOverlayVisible) return false; // уже скрыт — без изменений
                overlay.Visible = false;
                _prevOverlayVisible = false;
                _prevOverlayHwnd = IntPtr.Zero;
                _prevOverlayBounds = Rectangle.Empty;
                return true;
            }

            RECT r = GetClientBounds(hWnd);
            var newLoc = new Point(r.Left, r.Top);
            var newSz = new Size(r.Right - r.Left, r.Bottom - r.Top);
            var newBounds = new Rectangle(newLoc, newSz);

            bool posChanged = (hWnd != _prevOverlayHwnd || newBounds != _prevOverlayBounds);

            if (posChanged)
            {
                overlay.Location = newLoc;
                overlay.Size = newSz;
                overlay.Visible = true;
                _prevOverlayHwnd = hWnd;
                _prevOverlayBounds = newBounds;
                _prevOverlayVisible = true;
            }
            else if (!_prevOverlayVisible)
            {
                overlay.Visible = true;
                _prevOverlayVisible = true;
            }

            // DebugMode: обновляем точки только при изменении состава
            if (overlay.DebugMode)
            {
                int expectedCount = userActions.Count(a => a.IsSet && !a.UseSize);
                if (overlay.DebugPoints.Count != expectedCount)
                {
                    overlay.DebugPoints.Clear();
                    foreach (var a in userActions)
                    {
                        if (!a.IsSet || a.UseSize) continue;
                        overlay.DebugPoints.Add(new OverlayForm.DebugPoint
                        {
                            RelX = (float)a.RelX,
                            RelY = (float)a.RelY,
                            Label = a.Key == Keys.None ? a.DisplayName : $"[{a.Key}] {a.DisplayName}",
                            Color = Color.FromArgb(99, 102, 241),
                            ActionId = a.InternalID
                        });
                    }
                    overlay.InputPoint = isGlobalInputSet ? new OverlayForm.DebugPoint
                    {
                        RelX = (float)globalInputX,
                        RelY = (float)globalInputY,
                        Label = "INPUT",
                        Color = Color.FromArgb(251, 191, 36),
                        IsInput = true,
                        ActionId = -2
                    } : null;
                    return true; // пересчитали точки — нужна перерисовка
                }
                else
                {
                    foreach (var pt in overlay.DebugPoints)
                    {
                        var a = userActions.FirstOrDefault(x => x.InternalID == pt.ActionId);
                        if (a != null && !overlay.IsDragging) { pt.RelX = (float)a.RelX; pt.RelY = (float)a.RelY; }
                    }
                }
            }

            return posChanged; // перерисовываем только если окно переместилось
        }

        // ═══════════════════════════════════════════════════
        //  ЛИЦЕНЗИЯ
        // ═══════════════════════════════════════════════════

        private int _licenseDaysRemaining = -1;

        private async Task SendLicenseDaysToUI()
        {
            string savedKey = GetSavedLicenseKey();
            if (!string.IsNullOrEmpty(savedKey))
            {
                var result = LicenseManager.Validate(savedKey);
                if (result.Status == LicenseStatus.Valid && result.Expiry.HasValue)
                    _licenseDaysRemaining = (int)(result.Expiry.Value.Date - DateTime.Today).TotalDays;
                else if (result.Status == LicenseStatus.Valid)
                    _licenseDaysRemaining = 9999;
            }
            await PostToJS($"setLicenseExpiry({_licenseDaysRemaining})");
        }

        private string GetSavedLicenseKey()
        {
            return LicenseManager.LoadSavedKey();
        }

        // ═══════════════════════════════════════════════════════
        //  EnforceVersionCheck — жёсткая блокировка устаревших версий
        //
        //  ИЗМЕНЕНИЯ:
        //  - Консоль открывается автоматически при начале обновления
        //  - Каждый шаг логируется через AppLog
        //  - После DownloadAndInstallUpdate НЕ вызываем Application.Exit() сразу —
        //    программа остаётся живой пока пользователь не нажмёт OK в диалоге
        //  - Ошибки в catch теперь тоже логируются с полным стектрейсом
        // ═══════════════════════════════════════════════════════
        private async Task<bool> EnforceVersionCheck()
        {
            while (true)
            {
#pragma warning disable IDE0059
                string remoteVersion = "";
                string downloadUrl = "";
                string expectedHash = "";
#pragma warning restore IDE0059

                AppLog("ОБЩЕЕ", $"[Версия] Проверяем актуальность: текущая={CURRENT_VERSION}");
                // лог URL версии убран

                HttpClient client = null;
                try
                {
                    client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    // лог запроса убран
                    string json = await client.GetStringAsync(VERSION_URL);
                    // лог ответа убран
                    var ver = Newtonsoft.Json.Linq.JObject.Parse(json);
                    remoteVersion = ver["version"]?.Value<string>()?.Trim() ?? "";
                    downloadUrl = ver["download_url"]?.Value<string>()?.Trim()
                                    ?? ver["url"]?.Value<string>()?.Trim()
                                    ?? "";
                    expectedHash = ver["sha256"]?.Value<string>()?.Trim().ToUpper() ?? "";
                    // лог данных сервера убран
                }
                catch (Exception netEx)
                {
                    client?.Dispose();
                    AppLog("ОШИБКИ", $"[Версия] Ошибка сети: {netEx.GetType().Name}: {netEx.Message}");
                    var netDlg = ShowBlockingDialog(
                        title: "Нет соединения",
                        message: "Не удалось проверить версию программы.\n\nПроверьте подключение к интернету.",
                        btn1Text: "Повторить",
                        btn2Text: "Закрыть"
                    );
                    if (netDlg == DialogResult.Yes) continue;
                    Application.Exit();
                    return false;
                }

                // Версия актуальна — пропускаем
                // Защита от downgrade: если текущая >= удалённой — запуск разрешён
                bool versionIsOk;
                try
                {
                    var cur = new Version(CURRENT_VERSION.Replace(",", "."));
                    var rem = new Version(remoteVersion.Replace(",", "."));
                    versionIsOk = cur >= rem;
                }
                catch { versionIsOk = remoteVersion == CURRENT_VERSION; }

                if (versionIsOk)
                {
                    client?.Dispose();
                    AppLog("ОБЩЕЕ", $"[Версия] Актуальна — запуск разрешён");
                    return true;
                }

                AppLog("ОБЩЕЕ", $"[Версия] Требуется обновление: {CURRENT_VERSION} → {remoteVersion}");

                bool canAutoUpdate = !string.IsNullOrEmpty(downloadUrl)
                                     && downloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrEmpty(expectedHash);

                // лог canAutoUpdate убран

                string msg = $"Ваша версия ({CURRENT_VERSION}) устарела.\nДоступна версия {remoteVersion}.";

                var dlgResult = ShowBlockingDialog(
                    title: "Требуется обновление",
                    message: msg,
                    btn1Text: "Обновить",
                    btn2Text: "Отмена"
                );

                if (dlgResult == DialogResult.Yes)
                {
                    AppLog("ОБЩЕЕ", "══════════════════════════════════════");
                    AppLog("ОБЩЕЕ", $"[Обновление] Начато: {CURRENT_VERSION} → {remoteVersion}");
                    // лог URL обновления убран
                    // лог SHA256 убран
                    AppLog("ОБЩЕЕ", "══════════════════════════════════════");

                    if (canAutoUpdate)
                    {
                        // ── Запускаем обновление и ЖДЁМ завершения ──
                        // DownloadAndInstallUpdate сама вызовет Application.Exit()
                        // после того как пользователь нажмёт OK в финальном диалоге.
                        // Если установка провалится — ShowError уже показан, программа остаётся жить.
                        await DownloadAndInstallUpdate(downloadUrl, expectedHash, client, remoteVersion);

                        // Сюда попадаем только если установка НЕ удалась (ошибки #1001-#1008).
                        // В этом случае НЕ вызываем Application.Exit() — пусть пользователь
                        // читает консоль и решает что делать.
                        AppLog("ОШИБКИ", "[Обновление] Завершилось с ошибкой — программа остаётся открытой");
                        AppLog("ОШИБКИ", "[Обновление] Прочитайте код ошибки в диалоге выше и файл Коды_ошибок_обновления.md");
                        return false; // не пускаем в основное приложение
                    }
                    else
                    {
                        // лог URL браузера убран
                        string openUrl = string.IsNullOrEmpty(downloadUrl) ? SUPPORT_URL : downloadUrl;
                        try { System.Diagnostics.Process.Start(openUrl); } catch { }
                        MessageBox.Show(
                            "Программа будет закрыта.\nПосле скачивания запустите новую версию.",
                            "Обновление", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Application.Exit();
                        return false;
                    }
                }
                else
                {
                    // Пользователь нажал Отмена — старая версия не поддерживается
                    AppLog("ОБЩЕЕ", "[Версия] Пользователь отказался от обновления — закрываем программу");
                    MessageBox.Show(
                        "Старая версия больше не поддерживается.\n\nПожалуйста, обновите приложение.",
                        "Обновление обязательно", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                    return false;
                }
            }
        }

        // Модальный диалог без крестика, TopMost, нельзя закрыть кроме кнопок.
        private DialogResult ShowBlockingDialog(string title, string message, string btn1Text, string btn2Text)
        {
            // Диалог ОБЯЗАТЕЛЬНО показывать в UI-потоке, иначе клики не регистрируются
            if (InvokeRequired)
            {
                return (DialogResult)Invoke(new Func<DialogResult>(() =>
                    ShowBlockingDialog(title, message, btn1Text, btn2Text)));
            }

            var result = DialogResult.No;

            var dlg = new Form
            {
                Text = title,
                Size = new Size(420, 200),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = true,
                TopMost = false,
                BackColor = Color.FromArgb(14, 14, 22),
                KeyPreview = true,
            };
            // Escape закрывает как «Отмена»
            dlg.KeyDown += (fs, fe) => { if (fe.KeyCode == Keys.Escape) { result = DialogResult.No; dlg.Close(); } };

            var lbl = new Label
            {
                Text = message,
                ForeColor = Color.FromArgb(220, 220, 230),
                Font = new Font("Segoe UI", 10f),
                Bounds = new Rectangle(20, 20, 380, 80),
                TextAlign = ContentAlignment.MiddleCenter
            };

            var btn1 = new Button
            {
                Text = btn1Text,
                Bounds = new Rectangle(220, 120, 160, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(99, 102, 241),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn1.FlatAppearance.BorderSize = 0;
            btn1.Click += (bs, be) => { result = DialogResult.Yes; dlg.Close(); };

            var btn2 = new Button
            {
                Text = btn2Text,
                Bounds = new Rectangle(40, 120, 160, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 50, 65),
                ForeColor = Color.FromArgb(180, 180, 190),
                Font = new Font("Segoe UI", 9.5f),
                Cursor = Cursors.Hand
            };
            btn2.FlatAppearance.BorderSize = 0;
            btn2.Click += (bs, be) => { result = DialogResult.No; dlg.Close(); };

            dlg.Controls.AddRange(new Control[] { lbl, btn1, btn2 });
            dlg.ShowDialog(this);
            return result;
        }

        private async Task CheckForUpdates()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    string jsonText = await client.GetStringAsync(VERSION_URL);
                    var ver = JObject.Parse(jsonText);
                    string remoteVersion = ver["version"]?.Value<string>() ?? "";
                    string downloadUrl = ver["download_url"]?.Value<string>() ?? "";
                    string expectedHash = ver["sha256"]?.Value<string>()?.Trim().ToUpper() ?? "";

                    if (string.IsNullOrEmpty(remoteVersion) || remoteVersion == CURRENT_VERSION) return;

                    var res = MessageBox.Show(
                        $"Доступна новая версия {remoteVersion}!\n" +
                        $"Текущая: {CURRENT_VERSION}\n\n" +
                        "Скачать и установить обновление?",
                        "Обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                    if (res != DialogResult.Yes) return;

                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        if (string.IsNullOrEmpty(expectedHash))
                        {
                            MessageBox.Show(
                                "#1004",
                                "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        await DownloadAndInstallUpdate(downloadUrl, expectedHash, client, remoteVersion);
                    }
                    else
                    {
                        Process.Start(SUPPORT_URL);
                    }
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════
        //  DownloadAndInstallUpdate
        //
        //  ИЗМЕНЕНИЯ:
        //  - Каждый шаг теперь логируется через AppLog (видно в консоли)
        //  - Исключения логируются с полным типом и сообщением
        //  - Прогресс-бар не блокирует консоль
        //  - После ошибки программа НЕ закрывается — пользователь видит консоль
        //  - Application.Exit() вызывается ТОЛЬКО при успехе после OK от пользователя
        // ═══════════════════════════════════════════════════════
        private async Task DownloadAndInstallUpdate(string url, string expectedSha256, HttpClient client, string remoteVersion = "")
        {
            string exePath = Application.ExecutablePath;
            string exeDir = Path.GetDirectoryName(exePath);

            // лог exePath убран
            // лог exeDir убран

            if (exeDir.IndexOfAny(new char[] { '"', '&', '|', '<', '>', ';', '\n', '\r' }) >= 0)
            {
                AppLog("ОШИБКИ", $"[Update] #1008 — путь к папке содержит недопустимые символы: {"[путь скрыт]"}");
                MessageBox.Show("#1008\n\nПуть к папке программы содержит недопустимые символы.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Временные файлы обновления кладём во %TEMP% — туда всегда есть права
            string newExePath = Path.Combine(Path.GetTempPath(), "winhk3_update_new.exe");

            // лог newExePath убран

            bool _canClose = false;

            var prog = new Form
            {
                Text = "Обновление WinHK3",
                Size = new Size(460, 230),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = true,
                TopMost = true,
                BackColor = Color.FromArgb(14, 14, 22),
            };

            prog.FormClosing += (fs, fe) =>
            {
                if (!_canClose) fe.Cancel = true;
            };

            var lbl = new Label
            {
                Text = "Подготовка...",
                Bounds = new Rectangle(16, 16, 428, 22),
                ForeColor = Color.FromArgb(0, 229, 255),
                Font = new Font("Segoe UI", 10f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
            };

            var bar = new System.Windows.Forms.ProgressBar
            {
                Bounds = new Rectangle(16, 48, 428, 22),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous,
            };

            var lblPct = new Label
            {
                Text = "0%",
                Bounds = new Rectangle(16, 76, 428, 18),
                ForeColor = Color.FromArgb(140, 140, 160),
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
            };

            // Шаги обновления — визуальный индикатор этапов
            // ○ → ● по мере выполнения
            var lblSteps = new Label
            {
                Text = "○ Проверка   ○ Скачивание   ○ Проверка файла   ○ Установка   ○ Готово",
                Bounds = new Rectangle(16, 104, 428, 20),
                ForeColor = Color.FromArgb(70, 70, 90),
                Font = new Font("Segoe UI", 8f),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
            };

            var lblHint = new Label
            {
                Text = "Не закрывайте это окно...",
                Bounds = new Rectangle(16, 134, 428, 16),
                ForeColor = Color.FromArgb(60, 60, 80),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
            };

            // Подпись версии
            var lblVer = new Label
            {
                Text = "",
                Bounds = new Rectangle(16, 158, 428, 16),
                ForeColor = Color.FromArgb(50, 50, 70),
                Font = new Font("Segoe UI", 7.5f),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
            };

            prog.Controls.AddRange(new Control[] { lbl, bar, lblPct, lblSteps, lblHint, lblVer });
            prog.Show(this);
            lblVer.Text = $"1WIN TOOLS PRO  {CURRENT_VERSION} → {remoteVersion}";
            Application.DoEvents();

            void SetProgress(int pct, string text)
            {
                if (prog.IsDisposed) return;
                Action update = () =>
                {
                    if (prog.IsDisposed) return;
                    bar.Value = Math.Max(0, Math.Min(100, pct));
                    lbl.Text = text;
                    lblPct.Text = pct + "%";

                    // Обновляем индикатор шагов в зависимости от прогресса
                    // ● = текущий/выполненный шаг, ○ = ещё не выполнен
                    string s1 = pct >= 5 ? "●" : "○";
                    string s2 = pct >= 10 ? "●" : "○";
                    string s3 = pct >= 75 ? "●" : "○";
                    string s4 = pct >= 92 ? "●" : "○";
                    string s5 = pct >= 100 ? "●" : "○";
                    lblSteps.Text = $"{s1} Проверка   {s2} Скачивание   {s3} Проверка файла   {s4} Установка   {s5} Готово";

                    // Подсвечиваем текущий активный шаг голубым, остальные серым
                    lblSteps.ForeColor = pct >= 100
                        ? Color.FromArgb(0, 200, 120)   // зелёный при завершении
                        : Color.FromArgb(100, 100, 130);

                    Application.DoEvents();
                };
                if (prog.InvokeRequired) prog.Invoke(update);
                else update();
            }

            void ShowError(string code, string detail = "")
            {
                _canClose = true;
                try { if (!prog.IsDisposed) prog.Close(); } catch { }
                try { if (File.Exists(newExePath)) File.Delete(newExePath); } catch { }
                string fullMsg = string.IsNullOrEmpty(detail) ? code : $"{code}\n\n{detail}";
                AppLog("ОШИБКИ", $"[Update] Ошибка: {fullMsg}");
                MessageBox.Show(
                    fullMsg,
                    "Ошибка обновления",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            try
            {
                // ── Шаг 0: проверка URL ──
                SetProgress(5, "Проверка параметров...");
                // лог URL скачивания убран
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    ShowError("#1001", $"URL не начинается с https://\nURL: {url}");
                    return;
                }

                // ── Шаг 1: скачивание ──
                SetProgress(10, "Скачивание новой версии...");
                // лог шага 1 убран

                byte[] data = null;
                try
                {
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        // лог HTTP статуса убран
                        response.EnsureSuccessStatusCode();
                        long? total = response.Content.Headers.ContentLength;
                        // лог размера убран

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var ms = new MemoryStream())
                        {
                            var buffer = new byte[81920];
                            long read = 0;
                            int bytesRead;
                            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await ms.WriteAsync(buffer, 0, bytesRead);
                                read += bytesRead;
                                if (total.HasValue && total.Value > 0)
                                {
                                    int pct = 10 + (int)(read * 60L / total.Value);
                                    SetProgress(pct, $"Скачивание... {read / 1024} КБ из {total.Value / 1024} КБ");
                                }
                                else
                                {
                                    SetProgress(35, $"Скачивание... {read / 1024} КБ");
                                }
                            }
                            data = ms.ToArray();
                        }
                    }
                    // лог скачанных байт убран
                }
                catch (Exception dlEx)
                {
                    ShowError("#1002", $"Ошибка при скачивании:\n{dlEx.GetType().Name}: {dlEx.Message}");
                    return;
                }

                if (data == null || data.Length < 1024)
                {
                    ShowError("#1003", $"Файл слишком маленький или пустой: {data?.Length ?? 0} байт");
                    return;
                }

                // ── Шаг 2: SHA256 ──
                SetProgress(75, "Проверка целостности файла...");
                // лог шага 2 убран
                // лог ожидаемого SHA убран

                string actualHash = await Task.Run(() =>
                {
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    {
                        byte[] hashBytes = sha.ComputeHash(data);
                        return BitConverter.ToString(hashBytes).Replace("-", "").ToUpper();
                    }
                });

                // лог реального SHA убран
                // лог совпадения SHA убран

                if (actualHash != expectedSha256.ToUpper())
                {
                    ShowError("#1004", $"SHA256 не совпадает!\nОжидалось: {expectedSha256}\nПолучено:  {actualHash}");
                    return;
                }

                // ── Шаг 3: запись на диск ──
                SetProgress(85, "Сохранение файла...");
                // лог шага 3 убран
                try
                {
                    await Task.Run(() => File.WriteAllBytes(newExePath, data));
                    long savedSize = new FileInfo(newExePath).Length;
                    // лог записанных байт убран
                }
                catch (Exception writeEx)
                {
                    ShowError("#1005", $"Не удалось записать файл:\n{writeEx.GetType().Name}: {writeEx.Message}\nПуть: {newExePath}");
                    return;
                }

                // ── Шаг 4: скрипт обновления ──
                SetProgress(92, "Подготовка установщика...");
                int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                // лог шага 4 убран

                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WinHK3", "update_log.txt");

                // PowerShell-скрипт пишем во TEMP — туда всегда есть права на запись,
                // в отличие от папки рядом с exe (Program Files / антивирус / UAC)
                string ps1Path = Path.Combine(Path.GetTempPath(), $"winhk3_update_{currentPid}.ps1");
                string ps1 =
                    "$pid2wait = " + currentPid + "\r\n" +
                    "$src = '" + newExePath.Replace("'", "''") + "'\r\n" +
                    "$dst = '" + exePath.Replace("'", "''") + "'\r\n" +
                    "$log = '" + logPath.Replace("'", "''") + "'\r\n" +
                    "Add-Content $log '[PS] Старт обновления'\r\n" +
                    "while ($true) {\r\n" +
                    "    $p = Get-Process -Id $pid2wait -ErrorAction SilentlyContinue\r\n" +
                    "    if (-not $p) { break }\r\n" +
                    "    Start-Sleep -Milliseconds 500\r\n" +
                    "}\r\n" +
                    "Add-Content $log '[PS] Процесс завершён, копируем'\r\n" +
                    "$ok = $false\r\n" +
                    "for ($i = 0; $i -lt 20; $i++) {\r\n" +
                    "    try {\r\n" +
                    "        Copy-Item -Path $src -Destination $dst -Force -ErrorAction Stop\r\n" +
                    "        $ok = $true; break\r\n" +
                    "    } catch {\r\n" +
                    "        Add-Content $log \"[PS] Retry copy: $_\"\r\n" +
                    "        Start-Sleep -Seconds 1\r\n" +
                    "    }\r\n" +
                    "}\r\n" +
                    "if ($ok) {\r\n" +
                    "    Add-Content $log '[PS] Копирование OK'\r\n" +
                    "    Remove-Item $src -Force -ErrorAction SilentlyContinue\r\n" +
                    "    Add-Content $log '[PS] Запуск обновлённой программы'\r\n" +
                    "    Start-Process $dst\r\n" +
                    "    Add-Content $log '[PS] Готово'\r\n" +
                    "} else {\r\n" +
                    "    Add-Content $log '[PS] ОШИБКА: не удалось скопировать файл'\r\n" +
                    "}\r\n" +
                    "Remove-Item $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue\r\n";

                try
                {
                    await Task.Run(() => File.WriteAllText(ps1Path, ps1, Encoding.UTF8));
                    // лог пути скрипта убран
                }
                catch (Exception psWriteEx)
                {
                    ShowError("#1006", $"Не удалось создать скрипт обновления:\n{psWriteEx.Message}\nПуть: {ps1Path}");
                    try { if (File.Exists(newExePath)) File.Delete(newExePath); } catch { }
                    return;
                }

                // ── Шаг 5: запуск PowerShell скрипта ──
                SetProgress(97, "Запуск установщика...");
                // лог шага 5 убран

                bool launched = false;

                // Попытка 1: powershell.exe с обходом политики выполнения
                try
                {
                    var psi1 = new ProcessStartInfo("powershell.exe",
                        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1Path}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    var proc1 = Process.Start(psi1);
                    // лог PID PowerShell убран
                    launched = true;
                }
                catch (Exception ex1)
                {
                    AppLog("ОШИБКИ", $"[Update] PowerShell недоступен: {ex1.Message}, пробуем cmd...");
                }

                // Попытка 2: cmd.exe /c — передаём команды inline без .bat файла
                if (!launched)
                {
                    try
                    {
                        // Inline команда: ждём PID, копируем, запускаем
                        string cmdArgs =
                            $"/c \"(for /l %i in (1,1,60) do " +
                            $"(tasklist /fi \"PID eq {currentPid}\" 2>nul | find \"{currentPid}\" >nul && timeout /t 1 /nobreak >nul)) & " +
                            $"copy /y \\\"{newExePath}\\\" \\\"{exePath}\\\" && " +
                            $"del \\\"{newExePath}\\\" && " +
                            $"start \\\"\\\" \\\"{exePath}\\\"\"";

                        var psi2 = new ProcessStartInfo("cmd.exe", cmdArgs)
                        {
                            CreateNoWindow = true,
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        var proc2 = Process.Start(psi2);
                        // лог PID cmd убран
                        launched = true;
                    }
                    catch (Exception ex2)
                    {
                        AppLog("ОШИБКИ", $"[Update] cmd.exe тоже заблокирован: {ex2.Message}");
                    }
                }

                if (!launched)
                {
                    ShowError("#1007", "Не удалось запустить установщик обновления.\n\nАнтивирус блокирует запуск скриптов.\n\nДобавьте папку программы в исключения антивируса и попробуйте снова.");
                    try { if (File.Exists(newExePath)) File.Delete(newExePath); } catch { }
                    try { if (File.Exists(ps1Path)) File.Delete(ps1Path); } catch { }
                    return;
                }

                // ── Шаг 6: успех ──
                // Обновляем прогресс-бар до 100% и показываем финальное сообщение.
                // После нажатия OK — Application.Exit(), батник уже запущен и
                // ждёт завершения нашего процесса через цикл по PID.
                SetProgress(100, "✓ Готово! Перезапуск...");
                AppLog("ОБЩЕЕ", "[Update] ✓ Все шаги выполнены успешно!");
                // лог PID батника убран
                await Task.Delay(600);

                _canClose = true;
                try { if (!prog.IsDisposed) prog.Close(); } catch { }

                // лог финального диалога убран

                MessageBox.Show(
                    "Обновление загружено!\n\nПрограмма закроется и перезапустится автоматически.",
                    "Обновление WinHK3",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                AppLog("ОБЩЕЕ", "[Update] Пользователь нажал OK — завершаем программу, батник подхватит и установит");
                Application.Exit();
            }
            catch (Exception ex)
            {
                // Ловим всё что не поймали выше
                string detail = $"{ex.GetType().Name}: {ex.Message}\n\nStack:\n{ex.StackTrace}";
                AppLog("ОШИБКИ", $"[Update] #1008 — необработанное исключение:\n{detail}");
                ShowError("#1008", $"Необработанная ошибка:\n{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════
        //  СОХРАНЕНИЕ / ЗАГРУЗКА
        // ═══════════════════════════════════════════════════
        private void SaveLayoutsToFile()
        {
            try
            {
                var lines = new List<string>();
                foreach (var c in layouts)
                {
                    lines.Add("CONFIG|" + c.Name);
                    foreach (var s in c.Slots)
                        lines.Add($"{s.Rect.X},{s.Rect.Y},{s.Rect.Width},{s.Rect.Height},{s.Order},{(int)s.Type},{s.TypeIndex}");
                }
                File.WriteAllLines(layoutPath, lines);
            }
            catch (Exception ex) { AppLog("ОШИБКИ", $"SaveLayoutsToFile: {ex.Message}"); }
        }

        private void LoadLayoutsFromFile()
        {
            try
            {
                if (!File.Exists(layoutPath)) return;
                layouts.Clear(); LayoutConfig cur = null;
                foreach (var line in File.ReadAllLines(layoutPath))
                {
                    if (line.StartsWith("CONFIG|"))
                    {
                        var parts = line.Split(new[] { '|' }, 2);
                        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
                        { cur = new LayoutConfig { Name = parts[1] }; layouts.Add(cur); }
                    }
                    else if (cur != null)
                    {
                        var p = line.Split(',');
                        if (p.Length >= 5
                            && int.TryParse(p[0], out int rx) && int.TryParse(p[1], out int ry)
                            && int.TryParse(p[2], out int rw) && int.TryParse(p[3], out int rh)
                            && int.TryParse(p[4], out int ro)
                            && rw > 0 && rh > 0)
                        {
                            var sl = new TableSlot { Rect = new Rectangle(rx, ry, rw, rh), Order = ro };
                            // SlotType и TypeIndex — добавлены позже, обратная совместимость
                            if (p.Length >= 7 && int.TryParse(p[5], out int st) && int.TryParse(p[6], out int ti))
                            { sl.Type = (SlotType)st; sl.TypeIndex = ti; }
                            cur.Slots.Add(sl);
                        }
                        else
                            AppLog("ОШИБКИ", $"LoadLayoutsFromFile: пропущена некорректная строка [{line}]");
                    }
                }
                // Пересчитываем TypeIndex для старых лейаутов (где = 0)
                foreach (var cfg in layouts) RebuildTypeIndexes(cfg);
                if (layouts.Count > 0) activeLayout = layouts[0];
            }
            catch (Exception ex)
            {
                AppLog("ОШИБКИ", $"LoadLayoutsFromFile: {ex.Message}");
            }
        }

        /// <summary>Пересчитывает TypeIndex для каждого слота внутри своего типа (1-based).</summary>
        private static void RebuildTypeIndexes(LayoutConfig cfg)
        {
            var counters = new Dictionary<SlotType, int>
            {
                { SlotType.Default,  0 },
                { SlotType.Active,   0 },
                { SlotType.Inactive, 0 }
            };
            foreach (var s in cfg.Slots.OrderBy(x => x.Order))
            {
                counters[s.Type]++;
                s.TypeIndex = counters[s.Type];
            }
        }

        private static readonly string[] HARDCODED_BASE_ACTIONS = new[]
        {
            "OPEN 2.5|0|0|False|69|False|False|True|2,5|True",
            "FOLD|0.366812227074236|0.943916349809886|True|81|False|False|False|3|True",
            "CHECK|0.502242152466368|0.945827232796486|True|87|False|False|False|3|True",
            "3bet 10|0|0|False|84|False|False|True|10|False",
            "3bet 7.5|0.666167664670659|0.939393939393939|True|82|False|False|True|7,5|True",
            "Sit out|0.0538922155688623|0.825757575757576|True|189|False|False|False|3|True",
            "Raise|0.6201171875|0.9453125|True|79|False|False|False|3|True",
            "Sit Out Next BB|0.0537109375|0.875|True|8|False|False|False|3|True",
            "Fold Any 9max|0.0205078125|0.86328125|True|0|False|False|False|3|True",
        };
        private const double DEFAULT_GLOBAL_INPUT_X = 0.547076313181368;
        private const double DEFAULT_GLOBAL_INPUT_Y = 0.878238341968912;

        private void LoadBaseActionsFromGeneral()
        {
            userActions.RemoveAll(a => a.IsBase);
            var baseActions = new List<DynamicAction>();
            var noSizeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FOLD", "CHECK", "Sit out", "Sit Out Next BB", "Fold Any", "Fold Any 9max" };
            foreach (var line in HARDCODED_BASE_ACTIONS)
            {
                var a = new DynamicAction { InternalID = idCounter++, IsBase = true };
                ParseActionData(a, line);
                a.IsBase = true;
                a.HideSize = noSizeNames.Contains(a.DisplayName);
                baseActions.Add(a);
            }
            for (int i = baseActions.Count - 1; i >= 0; i--)
                userActions.Insert(0, baseActions[i]);
            if (!isGlobalInputSet)
            {
                globalInputX = DEFAULT_GLOBAL_INPUT_X;
                globalInputY = DEFAULT_GLOBAL_INPUT_Y;
                isGlobalInputSet = true;
            }
            AppLog("ДЕЙСТВИЯ", $"Базовые кнопки загружены: {baseActions.Count}");
        }

        private void LoadConverterSettings()
        {
            try
            {
                if (File.Exists(_converterSettingsPath))
                {
                    var obj = JObject.Parse(File.ReadAllText(_converterSettingsPath, Encoding.UTF8));
                    _converter.InputDir = obj["input_dir"]?.Value<string>() ?? "";
                    _converter.OutputDir = obj["output_dir"]?.Value<string>() ?? "";
                    TablesLog($"Загружены сохранённые настройки: InputDir='{_converter.InputDir}' | Существует: {Directory.Exists(_converter.InputDir)}");
                }
                else
                {
                    TablesLog("Файл настроек конвертера не найден — InputDir пустой.");
                }
            }
            catch (Exception ex)
            {
                TablesLog($"ОШИБКА загрузки настроек конвертера: {ex.Message}");
            }
        }

        private void SaveConverterSettings()
        {
            try
            {
                var obj = new JObject
                {
                    ["input_dir"] = _converter.InputDir,
                    ["output_dir"] = _converter.OutputDir
                };
                File.WriteAllText(_converterSettingsPath, obj.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // ── H2N Settings ──────────────────────────────────────────────────────────
        private readonly string _h2nSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "h2n_settings_cs.json");

        private void LoadH2NSettings()
        {
            try
            {
                if (File.Exists(_h2nSettingsPath))
                {
                    var obj = JObject.Parse(File.ReadAllText(_h2nSettingsPath, Encoding.UTF8));
                    _h2nReader.ColorMarkersPath = obj["colorMarkersPath"]?.Value<string>() ?? "";

                    if (obj["fishMarkers"] is JArray markersArr)
                        _h2nReader.MarkersFromJson(markersArr);
                    else if (obj["fishMarkerIds"] is JArray idsArr)
                        _h2nReader.SetFishMarkerIds(idsArr.Select(t => t.Value<string>()));
                    else
                    {
                        string legacy = obj["fishMarkerId"]?.Value<string>() ?? "";
                        if (!string.IsNullOrEmpty(legacy))
                            _h2nReader.SetFishMarkerIds(new[] { legacy });
                    }

                    _autoSitOutEnabled = obj["autoSitOut"]?.Value<bool>() ?? false;

                    if (!string.IsNullOrEmpty(_h2nReader.ColorMarkersPath))
                        System.Threading.Tasks.Task.Run(() => _h2nReader.PreloadAll());

                    // Загружаем персистентный кэш фишей
                    System.Threading.Tasks.Task.Run(() => LoadFishCache());
                }
            }
            catch { }
        }

        private void SaveH2NSettings()
        {
            try
            {
                var obj = new JObject
                {
                    ["colorMarkersPath"] = _h2nReader.ColorMarkersPath,
                    ["fishMarkers"] = _h2nReader.MarkersToJson(),
                    ["autoSitOut"] = _autoSitOutEnabled
                };
                File.WriteAllText(_h2nSettingsPath, obj.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // ── Tables 2: отдельные H2N настройки ──────────────────────────────────

        private readonly string _h2nT2SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "h2n_t2_settings_cs.json");

        private void LoadH2NSettingsT2()
        {
            try
            {
                if (!File.Exists(_h2nT2SettingsPath)) return;
                var obj = JObject.Parse(File.ReadAllText(_h2nT2SettingsPath, Encoding.UTF8));

                // Fish reader
                _t2H2nReader.ColorMarkersPath = obj["colorMarkersPath"]?.Value<string>() ?? "";
                if (obj["fishMarkers"] is JArray arr)
                    _t2H2nReader.MarkersFromJson(arr);
                else if (obj["fishMarkerIds"] is JArray ids)
                    _t2H2nReader.SetFishMarkerIds(ids.Select(t => t.Value<string>()));
                if (!string.IsNullOrEmpty(_t2H2nReader.ColorMarkersPath))
                    System.Threading.Tasks.Task.Run(() => _t2H2nReader.PreloadAll());

                // Reg reader
                _t2RegReader.ColorMarkersPath = obj["regColorMarkersPath"]?.Value<string>() ?? _t2H2nReader.ColorMarkersPath;
                if (obj["regMarkers"] is JArray regArr)
                    _t2RegReader.MarkersFromJson(regArr);
                if (!string.IsNullOrEmpty(_t2RegReader.ColorMarkersPath))
                    System.Threading.Tasks.Task.Run(() => _t2RegReader.PreloadAll());

                // Auto sit-out
                _t2SitOutEnabled = obj["t2SitOutEnabled"]?.Value<bool>() ?? false;
                _t2SitOutHands = obj["t2SitOutHands"]?.Value<int>() ?? 3;
                _t2SitOutAutoMode = obj["t2SitOutAuto"]?.Value<bool>() ?? false;
                _t2SitOutSnoozeMin = obj["t2SitOutSnooze"]?.Value<int>() ?? 5;

                T2Log($"[H2N] T2 fish={_t2H2nReader.FishMarkers.Count}, reg={_t2RegReader.FishMarkers.Count}, sitout={_t2SitOutEnabled}");
            }
            catch (Exception ex) { T2Log($"[H2N] Ошибка загрузки T2: {ex.Message}"); }
            SetupT2Watchers();
        }

        private void SaveH2NSettingsT2()
        {
            try
            {
                var obj = new JObject
                {
                    ["colorMarkersPath"] = _t2H2nReader.ColorMarkersPath,
                    ["fishMarkers"] = _t2H2nReader.MarkersToJson(),
                    ["regColorMarkersPath"] = _t2RegReader.ColorMarkersPath,
                    ["regMarkers"] = _t2RegReader.MarkersToJson(),
                    ["t2SitOutEnabled"] = _t2SitOutEnabled,
                    ["t2SitOutHands"] = _t2SitOutHands,
                    ["t2SitOutAuto"] = _t2SitOutAutoMode,
                    ["t2SitOutSnooze"] = _t2SitOutSnoozeMin
                };
                File.WriteAllText(_h2nT2SettingsPath, obj.ToString(), Encoding.UTF8);
            }
            catch { }
            SetupT2Watchers();
        }

        // ── FileSystemWatcher helpers ─────────────────────────────────────────
        private void SetupT2Watchers()
        {
            SetupOneWatcher(ref _t2FishWatcher, _t2H2nReader.ColorMarkersPath, "fish");
            string rp = _t2RegReader.ColorMarkersPath;
            if (!string.IsNullOrEmpty(rp) &&
                !string.Equals(rp, _t2H2nReader.ColorMarkersPath, StringComparison.OrdinalIgnoreCase))
                SetupOneWatcher(ref _t2RegWatcher, rp, "reg");
            else { _t2RegWatcher?.Dispose(); _t2RegWatcher = null; }
        }

        private void SetupOneWatcher(ref FileSystemWatcher w, string path, string tag)
        {
            w?.Dispose(); w = null;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            try
            {
                w = new FileSystemWatcher(path, "*.cm")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler h = (s, e2) => TriggerCmDebounce();
                w.Changed += h; w.Created += h; w.Deleted += h;
                w.Renamed += (s, e2) => TriggerCmDebounce();
                T2Log($"[FSW] watcher({tag}) → {path}");
            }
            catch (Exception ex) { T2Log($"[FSW] watcher({tag}) err: {ex.Message}"); }
        }

        private void TriggerCmDebounce()
        {
            _t2CmChangePending = true;
            if (_t2CmDebounceTimer != null && IsHandleCreated)
                BeginInvoke(new Action(() => { _t2CmDebounceTimer.Stop(); _t2CmDebounceTimer.Start(); }));
        }

        private void SaveAutoStartSettings()
        {
            try
            {
                var lines = new[]
                {
                    $"converter={_autoStartConverter}",
                    $"layout={_autoStartLayout}",
                    $"layoutName={_autoStartLayoutName}",
                    $"debugOverlay={overlay.DebugMode}",
                    $"tableBorderEnabled={_tableBorderEnabled}",
                    $"tableBorderColor=#{_tableBorderColor.R:X2}{_tableBorderColor.G:X2}{_tableBorderColor.B:X2}",
                    $"quickBetEnabled={string.Join(",", _quickBetActionIds)}"
                };
                File.WriteAllLines(_autoStartSettingsPath, lines, Encoding.UTF8);
            }
            catch { }
        }

        private void LoadAutoStartSettings()
        {
            try
            {
                if (!File.Exists(_autoStartSettingsPath)) return;
                foreach (var rawLine in File.ReadAllLines(_autoStartSettingsPath, Encoding.UTF8))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || !line.Contains('=')) continue;
                    var idx = line.IndexOf('=');
                    var key = line.Substring(0, idx);
                    var val = line.Substring(idx + 1);
                    switch (key)
                    {
                        case "master": break; // игнорируем — master убран из логики
                        case "converter": bool.TryParse(val, out _autoStartConverter); break;
                        case "layout": bool.TryParse(val, out _autoStartLayout); break;
                        case "layoutName": _autoStartLayoutName = val; break;
                        case "debugOverlay":
                            bool debugMode; bool.TryParse(val, out debugMode);
                            overlay.DebugMode = debugMode;
                            break;
                        case "tableBorderEnabled":
                            bool tbEnabled; bool.TryParse(val, out tbEnabled);
                            _tableBorderEnabled = tbEnabled;
                            overlay.ShowBorder = tbEnabled;
                            break;
                        case "tableBorderColor":
                            try
                            {
                                if (val.StartsWith("#") && val.Length == 7)
                                {
                                    int rc = Convert.ToInt32(val.Substring(1, 2), 16);
                                    int gc = Convert.ToInt32(val.Substring(3, 2), 16);
                                    int bc = Convert.ToInt32(val.Substring(5, 2), 16);
                                    _tableBorderColor = Color.FromArgb(rc, gc, bc);
                                    overlay.BorderColor = _tableBorderColor;
                                }
                            }
                            catch { }
                            break;
                        case "quickBetEnabled":
                            _quickBetActionIds.Clear();
                            foreach (var part in val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                                if (int.TryParse(part.Trim(), out int qbParsed)) _quickBetActionIds.Add(qbParsed);
                            break;
                    }
                }
                AppLog("ОБЩЕЕ", $"AutoStart настройки загружены: converter={_autoStartConverter} layout={_autoStartLayout} layoutName={_autoStartLayoutName} debugOverlay={overlay.DebugMode}");
            }
            catch (Exception ex)
            {
                AppLog("ОШИБКИ", $"LoadAutoStartSettings: {ex.Message}");
            }
        }

        private void LoadTableSize()
        {
            try
            {
                if (!File.Exists(tableSizePath)) return;
                foreach (var line in File.ReadAllLines(tableSizePath))
                {
                    if (line.StartsWith("width=")) int.TryParse(line.Split('=')[1], out _tableW);
                    if (line.StartsWith("height=")) int.TryParse(line.Split('=')[1], out _tableH);
                }
                if (_tableW > 0 && _tableH > 0)
                    AppLog("ОБЩЕЕ", $"Загружен размер стола: {_tableW}×{_tableH}");
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════
        //  SCREEN CAPTURE
        // ═══════════════════════════════════════════════════
        private List<string> GetAllTableTitles()
        {
            var titles = new List<string>();
            EnumWindows((hwnd, lp) =>
            {
                var sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                string title = sb.ToString();
                if (!string.IsNullOrEmpty(title) && IsTableWindow(title))
                    titles.Add(title);
                return true;
            }, IntPtr.Zero);
            return titles;
        }

        private List<IntPtr> GetAllTableHwnds()
        {
            var hwnds = new List<IntPtr>();
            EnumWindows((hwnd, lp) =>
            {
                var sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                string title = sb.ToString();
                if (!string.IsNullOrEmpty(title) && IsTableWindow(title))
                    hwnds.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return hwnds;
        }

        private IntPtr GetPrimaryTableHwnd()
        {
            // Если хоткей-режим активен — берём стол под курсором
            if (isRunning)
            {
                IntPtr underCursor = GetWindowUnderCursor();
                if (underCursor != IntPtr.Zero)
                {
                    var sb2 = new StringBuilder(256);
                    GetWindowText(underCursor, sb2, 256);
                    if (IsTableWindow(sb2.ToString())) return underCursor;
                }
            }
            // Fallback — первый найденный стол
            IntPtr result = IntPtr.Zero;
            EnumWindows((hWnd, lp) =>
            {
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                if (IsTableWindow(sb.ToString())) { result = hWnd; return false; }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private void StartScreenCapture(int intervalMs, string format, int quality, List<string> tables)
        {
            if (_screenRunning) StopScreenCapture();

            if (string.IsNullOrEmpty(_screenSaveFolder) || !Directory.Exists(_screenSaveFolder))
            {
                AppLog("ОШИБКИ", "Screen: папка не выбрана или не существует!");
                _ = PostToJS("setScreenRunning(false)");
                return;
            }

            _screenFormat = format;
            _screenQuality = quality;
            _screenTargetTables = tables;
            _screenRunning = true;
            _screenShotCount = 0;
            _screenTotalBytes = 0;

            AppLog("ОБЩЕЕ", $"Screen: захват запущен | интервал={intervalMs}мс | формат={format} | папка={_screenSaveFolder}");

            _screenTimer = new System.Threading.Timer(ScreenCaptureCallback, null, 0, intervalMs);
        }

        private void StopScreenCapture()
        {
            _screenRunning = false;
            _screenTimer?.Dispose();
            _screenTimer = null;
            AppLog("ОБЩЕЕ", $"Screen: захват остановлен. Сделано скриншотов: {_screenShotCount}");
        }

        private void ScreenCaptureCallback(object state)
        {
            if (!_screenRunning) return;

            try
            {
                var hwnds = GetAllTableHwnds();
                bool captureAll = _screenTargetTables.Contains("all");

                for (int i = 0; i < hwnds.Count; i++)
                {
                    if (!captureAll && !_screenTargetTables.Contains(i.ToString()))
                        continue;

                    IntPtr hwnd = hwnds[i];

                    try
                    {
                        GetClientRect(hwnd, out RECT client);
                        int w = client.Right - client.Left;
                        int h = client.Bottom - client.Top;
                        if (w <= 0 || h <= 0) continue;

                        Bitmap bmp = CaptureWindowViaPrintWindow(hwnd, w, h);
                        if (bmp == null) continue;

                        using (bmp)
                        {
                            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                            string fileName = $"table_{i + 1}_{timestamp}.{_screenFormat}";
                            string filePath = Path.Combine(_screenSaveFolder, fileName);

                            SaveBitmap(bmp, filePath);

                            long fileSize = new FileInfo(filePath).Length;
                            _screenTotalBytes += fileSize;
                            _screenShotCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog("ОШИБКИ", $"Screen: ошибка снимка стола #{i + 1}: {ex.Message}");
                    }
                }

                int totalCount = _screenShotCount;
                double totalMb = _screenTotalBytes / (1024.0 * 1024.0);
                this.BeginInvoke(new Action(() =>
                {
                    _ = PostToJS($"onScreenShot({totalCount}, {totalMb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)})");
                }));
            }
            catch (Exception ex)
            {
                AppLog("ОШИБКИ", $"Screen: ошибка в цикле захвата: {ex.Message}");
            }
        }

        private Bitmap CaptureWindowViaPrintWindow(IntPtr hwnd, int w, int h)
        {
            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                hdcMem = CreateCompatibleDC(hdcScreen);
                hbmp = CreateCompatibleBitmap(hdcScreen, w, h);
                IntPtr hOld = SelectObject(hdcMem, hbmp);

                bool ok = PrintWindow(hwnd, hdcMem, PW_CLIENTONLY | PW_RENDERFULLCONTENT);

                SelectObject(hdcMem, hOld);

                if (!ok)
                {
                    hOld = SelectObject(hdcMem, hbmp);
                    ok = PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT);
                    SelectObject(hdcMem, hOld);
                }

                if (!ok) return null;

                return System.Drawing.Image.FromHbitmap(hbmp);
            }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
                if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        private void SaveBitmap(Bitmap bmp, string filePath)
        {
            if (_screenFormat == "png")
            {
                bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            }
            else
            {
                var jpegEncoder = System.Drawing.Imaging.ImageCodecInfo
                    .GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);

                if (jpegEncoder != null)
                {
                    var encParams = new System.Drawing.Imaging.EncoderParameters(1);
                    encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, (long)_screenQuality);
                    bmp.Save(filePath, jpegEncoder, encParams);
                }
                else
                {
                    bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Jpeg);
                }
            }
        }

        private void SaveBaseState()
        {
            try
            {
                var lines = userActions
                    .Where(a => a.IsBase)
                    .Select(a => $"{a.DisplayName}|{(int)a.Key}|{a.UseCtrl}|{a.UseShift}|{a.UseSize}|{a.SizeValue}|{a.IsEnabled}|{a.RelX.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{a.RelY.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{a.IsSet}")
                    .ToList();
                File.WriteAllLines(baseStatePath, lines, Encoding.UTF8);
            }
            catch { }
        }

        private void LoadBaseState()
        {
            if (!File.Exists(baseStatePath)) return;
            try
            {
                foreach (var rawLine in File.ReadAllLines(baseStatePath, Encoding.UTF8))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    var p = line.Split('|');
                    if (p.Length < 7) continue;
                    string name = p[0];
                    var existing = userActions.FirstOrDefault(x => x.IsBase &&
                        string.Equals(x.DisplayName, name, StringComparison.OrdinalIgnoreCase));
                    if (existing == null) continue;
                    existing.Key = (Keys)int.Parse(p[1]);
                    existing.UseCtrl = bool.Parse(p[2]);
                    existing.UseShift = bool.Parse(p[3]);
                    existing.UseSize = bool.Parse(p[4]);
                    existing.SizeValue = p[5];
                    existing.IsEnabled = bool.Parse(p[6]);
                    if (p.Length > 9)
                    {
                        existing.RelX = double.Parse(p[7], System.Globalization.CultureInfo.InvariantCulture);
                        existing.RelY = double.Parse(p[8], System.Globalization.CultureInfo.InvariantCulture);
                        existing.IsSet = bool.Parse(p[9]);
                    }
                }
                AppLog("ДЕЙСТВИЯ", "Состояние базовых кнопок восстановлено из basestate.txt");
            }
            catch (Exception ex)
            {
                AppLog("ОШИБКИ", $"Ошибка LoadBaseState: {ex.Message}");
            }
        }

        private void SaveDataToFile(string path)
        {
            try
            {
                var lines = userActions.Select(a =>
                {
                    string prefix = a.IsBase ? "BASE_ACTION|" : "";
                    return prefix + $"{a.DisplayName}|{a.RelX.ToString(CultureInfo.InvariantCulture)}|{a.RelY.ToString(CultureInfo.InvariantCulture)}|{a.IsSet}|{(int)a.Key}|{a.UseCtrl}|{a.UseShift}|{a.UseSize}|{a.SizeValue}|{a.IsEnabled}";
                }).ToList();
                lines.Add($"GLOBAL_INPUT|{globalInputX.ToString(CultureInfo.InvariantCulture)}|{globalInputY.ToString(CultureInfo.InvariantCulture)}|{isGlobalInputSet}");
                File.WriteAllLines(path, lines, Encoding.UTF8);
            }
            catch { }
        }

        private void LoadDataFromFile(string path)
        {
            if (!File.Exists(path)) return;
            AppLog("ДЕЙСТВИЯ", $"Загрузка данных из: {path}");
            try
            {
                var baseActions = userActions.Where(a => a.IsBase).ToList();
                userActions.Clear();
                foreach (var ba in baseActions) userActions.Add(ba);

                foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    try
                    {
                        if (line.StartsWith("GLOBAL_INPUT"))
                        {
                            var p = line.Split('|');
                            globalInputX = double.Parse(p[1], CultureInfo.InvariantCulture);
                            globalInputY = double.Parse(p[2], CultureInfo.InvariantCulture);
                            isGlobalInputSet = bool.Parse(p[3]);
                        }
                        else if (line.StartsWith("BASE_ACTION|"))
                        {
                            var a2 = new DynamicAction { IsBase = true };
                            ParseActionData(a2, line.Substring("BASE_ACTION|".Length));
                            a2.IsBase = true;
                            var existing = userActions.FirstOrDefault(x => x.IsBase &&
                                string.Equals(x.DisplayName, a2.DisplayName, StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                            {
                                existing.Key = a2.Key; existing.UseCtrl = a2.UseCtrl; existing.UseShift = a2.UseShift;
                                existing.UseSize = a2.UseSize; existing.SizeValue = a2.SizeValue; existing.IsEnabled = a2.IsEnabled;
                                if (a2.IsSet) { existing.RelX = a2.RelX; existing.RelY = a2.RelY; existing.IsSet = true; }
                                AppLog("ДЕЙСТВИЯ", $"BASE восстановлен: [{a2.DisplayName}] key={(int)a2.Key} enabled={a2.IsEnabled} isSet={existing.IsSet}");
                            }
                            else
                            {
                                AppLog("ДЕЙСТВИЯ", $"BASE не найден: [{a2.DisplayName}] — пропущен");
                            }
                        }
                        else
                        {
                            var a = new DynamicAction { InternalID = idCounter++ };
                            ParseActionData(a, line);
                            userActions.Add(a);
                        }
                    }
                    catch (Exception lineEx)
                    {
                        AppLog("ОШИБКИ", $"Ошибка разбора строки [{line}]: {lineEx.Message}");
                    }
                }
                AppLog("ДЕЙСТВИЯ", $"Загрузка завершена. Всего действий: {userActions.Count}");
            }
            catch (Exception ex)
            {
                AppLog("ОШИБКИ", $"Критическая ошибка LoadDataFromFile: {ex.Message}");
            }
        }

        private void ParseActionData(DynamicAction a, string data)
        {
            try
            {
                var p = data.Split('|');
                if (p.Length < 9) throw new FormatException($"Недостаточно полей: {p.Length}");

                a.DisplayName = p[0];

                if (!double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double relX))
                    throw new FormatException($"RelX не число: {p[1]}");
                a.RelX = Math.Max(0.0, Math.Min(1.0, relX));

                if (!double.TryParse(p[2], System.Globalization.NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double relY))
                    throw new FormatException($"RelY не число: {p[2]}");
                a.RelY = Math.Max(0.0, Math.Min(1.0, relY));

                if (!bool.TryParse(p[3], out bool isSet))
                    throw new FormatException($"IsSet не bool: {p[3]}");
                a.IsSet = isSet;

                if (!int.TryParse(p[4], out int keyInt))
                    throw new FormatException($"Key не число: {p[4]}");
                if (!Enum.IsDefined(typeof(Keys), keyInt))
                    throw new FormatException($"Keys недопустимое значение: {keyInt}");
                a.Key = (Keys)keyInt;

                if (!bool.TryParse(p[5], out bool useCtrl)) throw new FormatException($"UseCtrl не bool: {p[5]}");
                if (!bool.TryParse(p[6], out bool useShift)) throw new FormatException($"UseShift не bool: {p[6]}");
                if (!bool.TryParse(p[7], out bool useSize)) throw new FormatException($"UseSize не bool: {p[7]}");
                a.UseCtrl = useCtrl;
                a.UseShift = useShift;
                a.UseSize = useSize;
                a.SizeValue = p[8];

                a.IsEnabled = p.Length <= 9 || !bool.TryParse(p[9], out bool en) || en;
            }
            catch (Exception ex)
            {
                AppLog("ОШИБКИ", $"ParseActionData: {ex.Message} | данные: [{data}]");
            }
        }

        // ═══════════════════════════════════════════════════
        //  FALLBACK HTML — показывается только если Embedded Resource не найден
        //  (т.е. ui.html не был добавлен в проект как Внедрённый ресурс)
        // ═══════════════════════════════════════════════════
        private string FallbackHtml() => @"
<!DOCTYPE html><html><head><meta charset='UTF-8'>
<style>body{background:#0e0e16;color:#e2e2e8;font-family:sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}
.box{text-align:center;padding:40px;border:1px solid #333;border-radius:12px;}
h2{color:#6366f1;}p{color:#888;}code{color:#fbbf24;}</style></head>
<body><div class='box'><h2>⚠ ui.html не найден</h2>
<p>Положи файл <code>ui.html</code> рядом с <code>.exe</code></p></div></body></html>";

        // ═══════════════════════════════════════════════════
        //  Мониторинг активных столов по HH файлам
        // ═══════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════
        //  TABLES 2 — чистый список файлов из InputDir
        //  Имя файла (без расширения) + дата последнего изменения
        //  Сортировка: свежие сверху
        // ══════════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════
        //  FISH CACHE — персистентный кэш ников-фишей
        //  Хранится в %AppData%\WinHK3ish_cache.json
        //  Формат: { "nick": { "isFish": bool, "color": "#hex", "checked": "ISO" }, ... }
        //  Ник перепроверяется если isFish=false или прошло > 5 минут
        // ══════════════════════════════════════════════════════

        private static readonly string _fishCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinHK3", "fish_cache.json");

        // ── Кэш ников: Fish и Reg ──────────────────────────────────────────────
        // Два независимых файла: fish_cache.json и reg_cache.json
        // FishCacheEntry.Kind: 0=unknown, 1=fish, 2=not_fish
        // RegCacheEntry.Kind:  0=unknown, 1=reg,  2=not_reg
        // Permanent fish: перепроверка раз в 5 мин
        // Non-permanent fish: перепроверка раз в 60 сек
        // Permanent reg: никогда не перепроверяется после первой записи
        // Non-permanent reg: перепроверка раз в 60 сек
        // ─────────────────────────────────────────────────────────────────────

        private sealed class NickCacheEntry
        {
            public bool IsMatch;          // true = fish или reg
            public string Color;          // hex, null для регов
            public bool IsPermanent;      // соответствует флагу Permanent в маркере
            public DateTime LastChecked;
        }

        // ── Fish cache ───────────────────────────────────────────
        private Dictionary<string, NickCacheEntry> _fishCache
            = new Dictionary<string, NickCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _fishCacheLock = new object();
        private DateTime _fishCacheLastSaved = DateTime.MinValue;

        // ── Reg cache ────────────────────────────────────────────
        private static readonly string _regCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinHK3", "reg_cache.json");
        private Dictionary<string, NickCacheEntry> _regCache
            = new Dictionary<string, NickCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _regCacheLock = new object();
        private DateTime _regCacheLastSaved = DateTime.MinValue;

        private static Dictionary<string, NickCacheEntry> LoadNickCache(string path)
        {
            var dict = new Dictionary<string, NickCacheEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return dict;
                var obj = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                foreach (var kv in obj)
                {
                    try
                    {
                        dict[kv.Key] = new NickCacheEntry
                        {
                            IsMatch = kv.Value["match"]?.Value<bool>() ?? false,
                            Color = kv.Value["color"]?.Value<string>(),
                            IsPermanent = kv.Value["perm"]?.Value<bool>() ?? false,
                            LastChecked = kv.Value["checked"]?.Value<DateTime>() ?? DateTime.MinValue
                        };
                    }
                    catch { }
                }
                System.Diagnostics.Debug.WriteLine($"[NickCache] Loaded {dict.Count} from {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NickCache] Load error {path}: {ex.Message}");
            }
            return dict;
        }

        private static void SaveNickCache(string path, Dictionary<string, NickCacheEntry> cache)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var obj = new JObject();
                foreach (var kv in cache)
                    obj[kv.Key] = new JObject
                    {
                        ["match"] = kv.Value.IsMatch,
                        ["color"] = kv.Value.Color,
                        ["perm"] = kv.Value.IsPermanent,
                        ["checked"] = kv.Value.LastChecked.ToString("o")
                    };
                File.WriteAllText(path, obj.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NickCache] Save error {path}: {ex.Message}");
            }
        }

        private void LoadFishCache()
        {
            var d = LoadNickCache(_fishCachePath);
            lock (_fishCacheLock) { _fishCache = d; }
        }

        private void LoadRegCache()
        {
            var d = LoadNickCache(_regCachePath);
            lock (_regCacheLock) { _regCache = d; }
        }

        private void SaveFishCache()
        {
            Dictionary<string, NickCacheEntry> snap;
            lock (_fishCacheLock) { snap = new Dictionary<string, NickCacheEntry>(_fishCache, StringComparer.OrdinalIgnoreCase); }
            SaveNickCache(_fishCachePath, snap);
            _fishCacheLastSaved = DateTime.Now;
        }

        private void SaveRegCache()
        {
            Dictionary<string, NickCacheEntry> snap;
            lock (_regCacheLock) { snap = new Dictionary<string, NickCacheEntry>(_regCache, StringComparer.OrdinalIgnoreCase); }
            SaveNickCache(_regCachePath, snap);
            _regCacheLastSaved = DateTime.Now;
        }

        // ── Ограничение размера кэша ников ───────────────────────────────────
        private const int NickCacheMax = 2000;
        private DateTime _fishCacheLastTrim = DateTime.MinValue;
        private DateTime _regCacheLastTrim = DateTime.MinValue;

        /// <summary>
        /// Удаляет устаревшие non-permanent записи. Вызывать под lock!
        /// </summary>
        private static void TrimNickCache(Dictionary<string, NickCacheEntry> cache, ref DateTime lastTrim)
        {
            var now = DateTime.Now;
            bool needTrim = (now - lastTrim).TotalMinutes > 10 || cache.Count > NickCacheMax;
            if (!needTrim) return;
            lastTrim = now;
            var del = cache
                .Where(kv => !kv.Value.IsPermanent && (now - kv.Value.LastChecked).TotalMinutes > 2)
                .Select(kv => kv.Key).ToList();
            foreach (var k in del) cache.Remove(k);
            if (cache.Count > NickCacheMax)
            {
                var oldest = cache.Where(kv => !kv.Value.IsPermanent)
                    .OrderBy(kv => kv.Value.LastChecked)
                    .Take(cache.Count - NickCacheMax + 200)
                    .Select(kv => kv.Key).ToList();
                foreach (var k in oldest) cache.Remove(k);
            }
        }

        /// <summary>
        /// Проверяет ник как FISH через H2NColorNoteReader.
        /// Кэш в Form1 нужен только для персистентного хранения результатов между сессиями.
        /// Внутренний кэш самого reader'а (H2NColorNoteReader._nickCache) обрабатывает
        /// повторные запросы и TTL. Здесь только сохраняем в файл для персистентности.
        /// </summary>
        private string GetFishColorCached(string nick)
        {
            if (string.IsNullOrEmpty(nick)) return null;

            // Читаем из Form1-кэша (быстрый путь для permanent-ников)
            lock (_fishCacheLock)
            {
                if (_fishCache.TryGetValue(nick, out var entry) && entry.IsPermanent)
                    return entry.IsMatch ? entry.Color : null;
            }

            // Делегируем в reader (он сам управляет TTL и файловым кэшем)
            string color = _t2H2nReader.GetFishColor(nick);
            bool isMatch = color != null;
            bool isPermanent = isMatch && _t2H2nReader.IsPermanent(nick);

            var e2 = new NickCacheEntry { IsMatch = isMatch, Color = color, IsPermanent = isPermanent, LastChecked = DateTime.Now };
            lock (_fishCacheLock) { _fishCache[nick] = e2; }
            if ((DateTime.Now - _fishCacheLastSaved).TotalSeconds > 30) SaveFishCache();
            return isMatch ? color : null;
        }

        /// <summary>
        /// Проверяет ник как REG через H2NColorNoteReader.
        /// </summary>
        private string GetRegColorCached(string nick)
        {
            if (string.IsNullOrEmpty(nick)) return null;
            if (_t2RegReader.FishMarkers.Count == 0) return null;

            lock (_regCacheLock)
            {
                if (_regCache.TryGetValue(nick, out var entry) && entry.IsPermanent)
                    return entry.IsMatch ? entry.Color : null;
            }

            string color = _t2RegReader.GetFishColor(nick);
            bool isMatch = color != null;
            bool isPermanent = isMatch && _t2RegReader.IsPermanent(nick);

            var e2 = new NickCacheEntry { IsMatch = isMatch, Color = color, IsPermanent = isPermanent, LastChecked = DateTime.Now };
            lock (_regCacheLock) { _regCache[nick] = e2; }
            if ((DateTime.Now - _regCacheLastSaved).TotalSeconds > 30) SaveRegCache();
            return isMatch ? color : null;
        }

        /// <summary>Извлекает ники всех игроков из последней раздачи файла HH.</summary>
        private static List<string> ExtractNicksFromLastHand(string filePath)
        {
            var nicks = new List<string>();
            try
            {
                string content = File.ReadAllText(filePath, Encoding.UTF8);
                // Берём только последнюю раздачу
                int lastIdx = content.LastIndexOf("1WinPoker Hand", StringComparison.Ordinal);
                if (lastIdx < 0) return nicks;
                string hand = content.Substring(lastIdx);
                // Seat #1: NickName (1234 in chips)
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    hand,
                    @"Seat #?\d+:\s+(.+?)\s+\([\d,. $]+\s*(?:in\s+chips)?\)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string nick = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(nick) && !nicks.Contains(nick))
                        nicks.Add(nick);
                }
            }
            catch { }
            return nicks;
        }

        // ── Tables 2 ──────────────────────────────────────────────────────────────

        private static readonly string _t2LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "tables2_log.txt");
        private static readonly object _t2LogLock = new object();
        private DateTime _t2LastLogFlush = DateTime.MinValue;

        private void T2Log(string msg)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                System.Diagnostics.Debug.WriteLine("[T2] " + msg);
                lock (_t2LogLock)
                    File.AppendAllText(_t2LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void T2LogInit()
        {
            try
            {
                File.WriteAllText(_t2LogPath,
                    $"=== Tables2 LOG — {DateTime.Now:dd.MM.yyyy HH:mm:ss} ==={Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
        }

        private int _tables2Guard = 0;

        /// <summary>
        /// Парсит имя файла. Возвращает (displayName, rawTableName).
        /// displayName = "Montana #5 0.15$-0.30$"
        /// rawTableName = "Montana #5"  (используется для поиска окна по заголовку)
        /// </summary>
        private static (string displayName, string rawTableName) ParseTable2Name(string filenameNoExt)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                filenameNoExt,
                @"^HH\d+\s+(.+?)\s+\(#\d+\)\s*-\s*([\d.,]+\s*\$\s*-\s*[\d.,]+\s*\$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (m.Success)
            {
                string rawName = m.Groups[1].Value.Trim();
                string stakes = System.Text.RegularExpressions.Regex.Replace(
                    m.Groups[2].Value, @"\s+", "");
                return (rawName + " " + stakes, rawName);
            }
            return (filenameNoExt, filenameNoExt);
        }

        /// <summary>
        /// Проверяет, открыто ли окно стола. Ищет rawTableName (без ставок) в заголовках окон.
        /// </summary>
        private bool IsTableWindowOpen(string rawTableName)
        {
            if (string.IsNullOrEmpty(rawTableName)) return false;
            var snapshot = GetTableWindowSnapshot();
            foreach (var entry in snapshot)
                if (entry.Title.IndexOf(rawTableName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private int _t2LogTickCount = 0;

        private void PushTables2Update()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _tables2Guard, 1, 0) != 0)
                return;

            try
            {
                string dir = _converter?.InputDir ?? "";
                var tablesList = new System.Collections.Generic.List<JObject>();

                bool fishEnabled = !string.IsNullOrEmpty(_t2H2nReader.ColorMarkersPath)
                                   && Directory.Exists(_t2H2nReader.ColorMarkersPath)
                                   && _t2H2nReader.FishMarkers.Count > 0;
                bool regEnabled = !string.IsNullOrEmpty(_t2RegReader.ColorMarkersPath)
                                   && Directory.Exists(_t2RegReader.ColorMarkersPath)
                                   && _t2RegReader.FishMarkers.Count > 0;

                bool doLog = (++_t2LogTickCount % 20 == 1);

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.txt"); }
                    catch { files = new string[0]; }

                    var snapshot = GetTableWindowSnapshot();
                    var openTitles = snapshot.Select(e => e.Title).ToList();

                    if (doLog)
                        T2Log($"Scan: files={files.Length}, wins={openTitles.Count}, fish={fishEnabled}({_t2H2nReader.FishMarkers.Count}m), reg={regEnabled}({_t2RegReader.FishMarkers.Count}m)");

                    // Парсим все файлы
                    var rawEntries = new System.Collections.Generic.List<(string displayName, string rawName, string filePath, DateTime dt)>();
                    foreach (var f in files)
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            var (disp, raw) = ParseTable2Name(Path.GetFileNameWithoutExtension(fi.Name));
                            rawEntries.Add((disp, raw, f, fi.LastWriteTime));
                        }
                        catch { }
                    }

                    // Дедупликация: один rawName — самый свежий файл
                    var byRaw = new Dictionary<string, (string displayName, string filePath, DateTime dt)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (dn, rn, fp, dt) in rawEntries)
                    {
                        if (!byRaw.ContainsKey(rn) || dt > byRaw[rn].dt)
                            byRaw[rn] = (dn, fp, dt);
                    }

                    var now = DateTime.Now;
                    foreach (var kv in byRaw)
                    {
                        string rawName = kv.Key;
                        var (displayName, filePath, dt) = kv.Value;

                        int ageSeconds = (int)(now - dt).TotalSeconds;
                        bool windowOpen = openTitles.Any(t =>
                            t.IndexOf(rawName, StringComparison.OrdinalIgnoreCase) >= 0);
                        bool isActive = ageSeconds < 90 && windowOpen;
                        bool isOpenIdle = windowOpen && !isActive;

                        int fishCount = 0;
                        string fishColor = null;
                        int regCount = 0;
                        string regColor = null;
                        var nickArr = new JArray();

                        if (isActive)
                        {
                            List<string> nicks = null;
                            try { nicks = ExtractNicksFromLastHand(filePath); }
                            catch { nicks = new List<string>(); }

                            if (nicks != null && nicks.Count > 0)
                            {
                                var fishNickLog = new List<string>();
                                foreach (var nick in nicks)
                                {
                                    string fc = fishEnabled ? GetFishColorCached(nick) : null;
                                    string rc = (regEnabled && fc == null) ? GetRegColorCached(nick) : null;
                                    // type: "fish" / "reg" / "unknown" (анонимус — нет маркера)
                                    string typ = fc != null ? "fish" : rc != null ? "reg" : "unknown";
                                    nickArr.Add(new JObject
                                    {
                                        ["nick"] = nick,
                                        ["type"] = typ,
                                        ["color"] = fc ?? rc  // null для анонимусов
                                    });
                                    if (fc != null) { fishCount++; if (fishColor == null) fishColor = fc; fishNickLog.Add(nick); }
                                    if (rc != null) { regCount++; if (regColor == null) regColor = rc; }
                                }
                                if (doLog || fishNickLog.Count > 0)
                                    T2Log($"ACTIVE '{displayName}': nicks={nicks.Count} fish={fishCount}" +
                                          (fishNickLog.Count > 0 ? $"[{string.Join(",", fishNickLog)}]" : "") +
                                          (regEnabled ? $" reg={regCount}" : ""));
                            }
                            else if (doLog)
                                T2Log($"ACTIVE '{displayName}': age={ageSeconds}s, fishH2N=off");

                            if (_t2SitOutEnabled && fishEnabled)
                                CheckT2SitOutByHands(displayName, rawName, filePath, fishCount);
                        }
                        else if (doLog && ageSeconds < 300)
                            T2Log($"idle '{displayName}': age={ageSeconds}s, win={windowOpen}");

                        tablesList.Add(new JObject
                        {
                            ["name"] = displayName,
                            ["rawName"] = rawName,   // для матчинга с заголовком окна в AutoLayoutTick
                            ["ageSeconds"] = ageSeconds,
                            ["isActive"] = isActive,
                            ["isOpenIdle"] = isOpenIdle,
                            ["fishCount"] = fishCount,
                            ["fishColor"] = fishColor,
                            ["regCount"] = regCount,
                            ["regColor"] = regColor,
                            ["nicks"] = nickArr
                        });
                    }

                    // ── Второй проход: окна столов, у которых НЕТ HH-файла ─────────
                    // Если стол открыт, но ни одного файла в InputDir для него нет —
                    // показываем его с именем из заголовка окна, isActive=false, isOpenIdle=true.
                    var coveredRawNames = new HashSet<string>(
                        byRaw.Keys, StringComparer.OrdinalIgnoreCase);

                    foreach (var winEntry in snapshot)
                    {
                        string title = winEntry.Title;
                        // Проверяем: не покрыт ли этот тайтл уже каким-то rawName из byRaw
                        bool alreadyCovered = false;
                        foreach (var rn in coveredRawNames)
                        {
                            if (title.IndexOf(rn, StringComparison.OrdinalIgnoreCase) >= 0)
                            { alreadyCovered = true; break; }
                        }
                        if (alreadyCovered) continue;

                        // Извлекаем имя стола из заголовка окна.
                        // Заголовок 1win обычно выглядит как:  "Montana #5 - Hold'em NL - 0.15$-0.30$"
                        // Берём часть до первого " - " (или весь тайтл если нет разделителя)
                        string tableName = title;
                        int dashIdx = title.IndexOf(" - ", StringComparison.Ordinal);
                        if (dashIdx > 0) tableName = title.Substring(0, dashIdx).Trim();
                        if (string.IsNullOrWhiteSpace(tableName)) tableName = title;

                        tablesList.Add(new JObject
                        {
                            ["name"] = tableName,
                            ["rawName"] = tableName,
                            ["ageSeconds"] = 99999,
                            ["isActive"] = false,
                            ["isOpenIdle"] = true,
                            ["fishCount"] = 0,
                            ["fishColor"] = (string)null,
                            ["regCount"] = 0,
                            ["regColor"] = (string)null,
                            ["nicks"] = new JArray()
                        });
                    }
                }
                else
                {
                    if (doLog) T2Log($"InputDir не задан: '{dir}'");
                    // InputDir не задан — всё равно показываем открытые окна столов
                    var snapshot2 = GetTableWindowSnapshot();
                    foreach (var winEntry in snapshot2)
                    {
                        string title = winEntry.Title;
                        string tableName = title;
                        int di = title.IndexOf(" - ", StringComparison.Ordinal);
                        if (di > 0) tableName = title.Substring(0, di).Trim();
                        if (string.IsNullOrWhiteSpace(tableName)) tableName = title;

                        tablesList.Add(new JObject
                        {
                            ["name"] = tableName,
                            ["rawName"] = tableName,
                            ["ageSeconds"] = 99999,
                            ["isActive"] = false,
                            ["isOpenIdle"] = true,
                            ["fishCount"] = 0,
                            ["fishColor"] = (string)null,
                            ["regCount"] = 0,
                            ["regColor"] = (string)null,
                            ["nicks"] = new JArray()
                        });
                    }
                }

                // Сортировка: открытые сверху (active > openIdle > closed), внутри по возрасту
                tablesList.Sort((a, b) =>
                {
                    bool aActive = a["isActive"].Value<bool>();
                    bool bActive = b["isActive"].Value<bool>();
                    bool aOpen = aActive || a["isOpenIdle"].Value<bool>();
                    bool bOpen = bActive || b["isOpenIdle"].Value<bool>();
                    if (aOpen != bOpen) return aOpen ? -1 : 1;
                    if (aOpen && bOpen && aActive != bActive) return aActive ? -1 : 1;
                    return a["ageSeconds"].Value<int>().CompareTo(b["ageSeconds"].Value<int>());
                });

                // ── Обновляем _lastTableInfos — используется AutoLayoutTick ──────
                // Active = стол с HH-активностью за последние 90 сек (isActive)
                // Fishy = за столом есть фиш
                // isOpenIdle = окно открыто но HH-активности нет → НЕ Active
                // ВАЖНО: Name = rawName (без ставок) — именно по нему матчится заголовок окна
                var newInfos = new List<ActiveTableInfo>();
                foreach (var t in tablesList)
                {
                    newInfos.Add(new ActiveTableInfo
                    {
                        Name = t["rawName"]?.Value<string>() ?? t["name"]?.Value<string>() ?? "",
                        Active = t["isActive"]?.Value<bool>() ?? false,
                        Fishy = (t["fishCount"]?.Value<int>() ?? 0) > 0,
                        SecondsAgo = t["ageSeconds"]?.Value<int>() ?? 9999,
                    });
                }
                _lastTableInfos = newInfos;

                var payload = new JObject
                {
                    ["inputDir"] = dir,
                    ["tables"] = new JArray(tablesList)
                };
                _ = PostToJS("showTables2(" + payload.ToString(Newtonsoft.Json.Formatting.None) + ")");
            }
            catch (Exception ex) { T2Log($"PushTables2Update EX: {ex.Message}"); }
            finally { System.Threading.Interlocked.Exchange(ref _tables2Guard, 0); }
        }

        /// <summary>
        /// Авто-ситаут по РАЗДАЧАМ без фиша (не по тикам таймера).
        /// </summary>
        private void CheckT2SitOutByHands(string displayName, string rawName, string filePath, int fishCount)
        {
            try
            {
                T2SitOutHandState st;
                lock (_t2SitOutHLock)
                {
                    if (!_t2SitOutHSt.TryGetValue(displayName, out st))
                    { st = new T2SitOutHandState(); _t2SitOutHSt[displayName] = st; }
                }

                string content;
                try { content = File.ReadAllText(filePath, Encoding.UTF8); }
                catch { return; }

                var hands = SplitHandsT2(content);
                if (hands.Count == 0) return;

                bool trigger = false;
                lock (_t2SitOutHLock)
                {
                    // Первый вызов — просто запомнить текущую раздачу
                    if (st.LastHandId == null)
                    { st.LastHandId = hands[hands.Count - 1].Id; return; }

                    int startIdx = 0;
                    for (int i = 0; i < hands.Count; i++)
                        if (hands[i].Id == st.LastHandId) { startIdx = i + 1; break; }

                    for (int i = startIdx; i < hands.Count; i++)
                    {
                        if (!HandHasSeatsT2(hands[i].Text)) continue;
                        if (HandHasFishT2(hands[i].Text))
                        { st.NoFishStreak = 0; st.AlertShown = false; }
                        else
                            st.NoFishStreak++;
                    }
                    st.LastHandId = hands[hands.Count - 1].Id;

                    if (st.NoFishStreak >= _t2SitOutHands
                        && !st.AlertShown
                        && DateTime.Now > st.SnoozedUntil)
                    {
                        st.AlertShown = true;
                        trigger = true;
                    }
                }

                if (!trigger) return;

                if (_t2SitOutAutoMode)
                {
                    var hwnd = FindTableWindowByName(rawName);
                    if (hwnd != IntPtr.Zero)
                    {
                        ExecuteSitOutNextBB(hwnd);
                        int streak;
                        lock (_t2SitOutHLock) { streak = st.NoFishStreak; st.NoFishStreak = 0; st.SnoozedUntil = DateTime.Now.AddMinutes(_t2SitOutSnoozeMin); }
                        T2Log($"[SitOut] AUTO '{displayName}': {streak} hands w/o fish");
                    }
                }
                else
                    this.BeginInvoke(new Action(() => ShowT2FishAlert(displayName, rawName)));
            }
            catch (Exception ex) { T2Log($"[SitOut] err: {ex.Message}"); }
        }

        // ── Хелперы для CheckT2SitOutByHands ─────────────────────────────────
        private static List<(string Id, string Text)> SplitHandsT2(string content)
        {
            var result = new List<(string, string)>();
            foreach (var part in content.Split(new[] { "1WinPoker Hand" }, StringSplitOptions.None))
            {
                if (!part.Contains("*** SUMMARY ***")) continue;
                string full = "1WinPoker Hand" + part;
                var m = System.Text.RegularExpressions.Regex.Match(full, @"1WinPoker Hand #?(\d+)");
                if (m.Success) result.Add((m.Groups[1].Value, full.Trim()));
            }
            return result;
        }

        private bool HandHasFishT2(string handText)
        {
            var ms = System.Text.RegularExpressions.Regex.Matches(handText,
                @"Seat #?\d+:\s+(.+?)\s+\([\d,. $]+\s*(?:in\s+chips)?\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in ms)
            {
                string nick = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(nick) && _t2H2nReader.IsFish(nick)) return true;
            }
            return false;
        }

        private static bool HandHasSeatsT2(string handText) =>
            System.Text.RegularExpressions.Regex.IsMatch(handText,
                @"Seat #?\d+:\s+\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Проверяет нужно ли сит-аут (устаревший вызов — теперь делегирует на CheckT2SitOutByHands).
        /// </summary>
        private void CheckT2SitOut(string displayName, string rawName, string filePath, int fishCount)
        {
            CheckT2SitOutByHands(displayName, rawName, filePath, fishCount);
        }

        private readonly Dictionary<string, FishAlertForm> _t2FishAlerts
            = new Dictionary<string, FishAlertForm>(StringComparer.OrdinalIgnoreCase);

        private void ShowT2FishAlert(string displayName, string rawName)
        {
            if (_t2FishAlerts.TryGetValue(displayName, out var ex2) &&
                ex2 != null && !ex2.IsDisposed && ex2.Visible) return;

            IntPtr hwnd = FindTableWindowByName(rawName);
            Rectangle rect = hwnd != IntPtr.Zero
                ? GetClientBoundsAsRectangle(hwnd)
                : new Rectangle(Screen.PrimaryScreen.WorkingArea.Width / 2 - 130,
                                Screen.PrimaryScreen.WorkingArea.Height / 2 - 46, 260, 92);

            var alert = new FishAlertForm { TableName = displayName, TableHwnd = hwnd };
            alert.PositionOverTable(rect);

            alert.OnSitOut += () =>
            {
                ExecuteSitOutNextBB(hwnd != IntPtr.Zero ? hwnd : FindTableWindowByName(rawName));
                T2Log($"[SitOut] CONFIRMED '{displayName}'");
                lock (_t2SitOutHLock)
                {
                    if (_t2SitOutHSt.TryGetValue(displayName, out var s))
                    { s.NoFishStreak = 0; s.AlertShown = false; s.SnoozedUntil = DateTime.Now.AddMinutes(_t2SitOutSnoozeMin); }
                }
                lock (_t2SitOutLock) { _t2SitOutState[displayName] = (0, DateTime.Now.AddMinutes(_t2SitOutSnoozeMin)); }
                _t2FishAlerts.Remove(displayName);
            };
            alert.OnStay += () =>
            {
                T2Log($"[SitOut] SKIPPED '{displayName}', snooze={_t2SitOutSnoozeMin}min");
                lock (_t2SitOutHLock)
                {
                    if (_t2SitOutHSt.TryGetValue(displayName, out var s))
                    { s.NoFishStreak = 0; s.AlertShown = false; s.SnoozedUntil = DateTime.Now.AddMinutes(_t2SitOutSnoozeMin); }
                }
                lock (_t2SitOutLock) { _t2SitOutState[displayName] = (0, DateTime.Now.AddMinutes(_t2SitOutSnoozeMin)); }
                _t2FishAlerts.Remove(displayName);
            };
            alert.FormClosed += (s, e2) => _t2FishAlerts.Remove(displayName);

            _t2FishAlerts[displayName] = alert;
            alert.Show();
        }

        //  Fish Monitor callbacks
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Вызывается из background-потока FishMonitor.
        /// Переключаемся в UI-поток и показываем уведомление.
        /// </summary>
        private void OnFishMonitorAlert(string tableName)
        {
            this.BeginInvoke(new Action(() => ShowFishAlert(tableName)));
        }

        /// <summary>Показывает FishAlertForm поверх стола. UI-поток.</summary>
        private void ShowFishAlert(string tableName)
        {
            // Если алерт уже показан для этого стола — не дублируем
            if (_fishAlerts.TryGetValue(tableName, out var existing) &&
                existing != null && !existing.IsDisposed && existing.Visible)
                return;

            // Ищем окно стола по имени
            IntPtr tableHwnd = FindTableWindowByName(tableName);
            Rectangle tableRect = tableHwnd != IntPtr.Zero
                ? GetClientBoundsAsRectangle(tableHwnd)
                : new Rectangle(Screen.PrimaryScreen.WorkingArea.Width / 2 - 130,
                                Screen.PrimaryScreen.WorkingArea.Height / 2 - 46, 260, 92);

            var alert = new FishAlertForm { TableName = tableName, TableHwnd = tableHwnd };
            alert.PositionOverTable(tableRect);

            alert.OnSitOut += () =>
            {
                _fishMonitor?.Reset(tableName);
                ExecuteSitOutNextBB(tableHwnd != IntPtr.Zero ? tableHwnd : FindTableWindowByName(tableName));
                _fishAlerts.Remove(tableName);
            };
            alert.OnStay += () =>
            {
                _fishMonitor?.Snooze(tableName);
                _fishAlerts.Remove(tableName);
            };
            alert.FormClosed += (s, e) => _fishAlerts.Remove(tableName);

            _fishAlerts[tableName] = alert;
            alert.Show();
        }

        /// <summary>Нажимает «Sit Out Next BB» по координатам из userActions для нужного окна стола.</summary>
        private void ExecuteSitOutNextBB(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            var sitOutAction = userActions.FirstOrDefault(a =>
                a.IsBase &&
                a.IsSet &&
                string.Equals(a.DisplayName, "Sit Out Next BB", StringComparison.OrdinalIgnoreCase));
            if (sitOutAction == null) return;

            System.Threading.Tasks.Task.Run(() =>
            {
                RECT r = GetClientBounds(hwnd);
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return;
                int cx = (int)(w * sitOutAction.RelX);
                int cy = (int)(h * sitOutAction.RelY);
                SilentClick(hwnd, cx, cy);
                AppLog("СТОЛЫ", $"Fish Auto-SitOut: {cx},{cy} → hwnd={hwnd}");
            });
        }

        /// <summary>Ищет окно стола по частичному совпадению с именем стола.</summary>
        private IntPtr FindTableWindowByName(string tableName)
        {
            IntPtr found = IntPtr.Zero;
            if (string.IsNullOrEmpty(tableName)) return found;
            EnumWindows((hWnd, lp) =>
            {
                var sb = new StringBuilder(512); GetWindowText(hWnd, sb, 512);
                string title = sb.ToString();
                if (IsTableWindow(title) && title.IndexOf(tableName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = hWnd;
                    return false; // stop
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Возвращает клиентский прямоугольник как System.Drawing.Rectangle.</summary>
        private Rectangle GetClientBoundsAsRectangle(IntPtr hwnd)
        {
            RECT r = GetClientBounds(hwnd);
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            LicenseManager.CancelPendingNotifications(); // отменяем pending Telegram-запросы
            _autoSeat?.Stop();
            StopScreenCapture();
            _t2FishWatcher?.Dispose();
            _t2RegWatcher?.Dispose();
            if (_hookID != IntPtr.Zero) UnhookWindowsHookEx(_hookID);
            base.OnFormClosing(e);
        }
    }

    //  КОНСОЛЬНОЕ ОКНО
    // ═══════════════════════════════════════════════════════
    public class ConsoleForm : Form
    {
        private readonly TabControl _tabs;
        private readonly Dictionary<string, RichTextBox> _panes = new Dictionary<string, RichTextBox>();
        private static readonly string[] TAB_NAMES = { "ОБЩЕЕ", "ОШИБКИ", "ДЕЙСТВИЯ", "РЕАКЦИИ", "РУМ" };
        private static readonly Color[] TAB_COLORS = {
            Color.FromArgb(200,200,200), Color.FromArgb(239,68,68),
            Color.FromArgb(99,102,241), Color.FromArgb(34,197,94), Color.FromArgb(251,191,36)
        };
        private const int MAX_LINES = 500;

        public ConsoleForm()
        {
            Text = "1WIN TOOLS — Консоль";
            Size = new Size(820, 480);
            MinimumSize = new Size(500, 300);
            BackColor = Color.FromArgb(10, 10, 16);
            ForeColor = Color.FromArgb(220, 220, 220);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(20, 20);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            Font = new Font("Consolas", 9f);

            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                ItemSize = new Size(80, 26),
                Padding = new Point(6, 4),
                BackColor = Color.FromArgb(14, 14, 22)
            };
            _tabs.DrawItem += (s, e) => {
                var tb = _tabs.TabPages[e.Index];
                var brush = e.Index == _tabs.SelectedIndex
                    ? new SolidBrush(Color.FromArgb(28, 28, 45))
                    : new SolidBrush(Color.FromArgb(14, 14, 22));
                e.Graphics.FillRectangle(brush, e.Bounds);
                var color = TAB_COLORS[e.Index];
                using (var fp = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                    e.Graphics.DrawString(tb.Text, fp, new SolidBrush(color),
                        e.Bounds.X + 6, e.Bounds.Y + 5);
            };

            for (int i = 0; i < TAB_NAMES.Length; i++)
            {
                var tp = new TabPage(TAB_NAMES[i]) { BackColor = Color.FromArgb(10, 10, 16), Padding = new Padding(0) };
                var rtb = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    BackColor = Color.FromArgb(8, 8, 14),
                    ForeColor = Color.FromArgb(200, 200, 200),
                    BorderStyle = BorderStyle.None,
                    ScrollBars = RichTextBoxScrollBars.Vertical,
                    Font = new Font("Consolas", 9f),
                    WordWrap = false
                };
                tp.Controls.Add(rtb);
                _tabs.TabPages.Add(tp);
                _panes[TAB_NAMES[i]] = rtb;
            }

            var btnClear = new Button
            {
                Text = "CLR",
                Dock = DockStyle.Bottom,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(28, 28, 42),
                ForeColor = Color.FromArgb(150, 150, 170),
                Font = new Font("Segoe UI", 8f)
            };
            btnClear.FlatAppearance.BorderSize = 0;
            btnClear.Click += (s, e) => { foreach (var p in _panes.Values) p.Clear(); };

            var btnOpenLog = new Button
            {
                Text = "📋 Открыть лог",
                Dock = DockStyle.Bottom,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(20, 40, 60),
                ForeColor = Color.FromArgb(100, 180, 255),
                Font = new Font("Segoe UI", 8f)
            };
            btnOpenLog.FlatAppearance.BorderSize = 0;
            btnOpenLog.Click += (s, e) => {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WinHK3", "update_log.txt");
                if (File.Exists(logPath))
                    System.Diagnostics.Process.Start("notepad.exe", logPath);
                else
                    MessageBox.Show("Файл update_log.txt не найден.", "Лог", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            Controls.Add(_tabs);
            Controls.Add(btnClear);
            Controls.Add(btnOpenLog);
        }

        public void Log(string category, string message)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(category, message))); return; }

            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            string line = $"[{ts}] {message}";
            Color col = GetColor(category);

            AppendLine("ОБЩЕЕ", $"[{category}] {line}", col);
            if (_panes.ContainsKey(category))
                AppendLine(category, line, col);
        }

        private void AppendLine(string tab, string line, Color col)
        {
            if (!_panes.TryGetValue(tab, out var rtb)) return;
            int start = rtb.TextLength;
            rtb.AppendText(line + "\n");
            rtb.Select(start, line.Length);
            rtb.SelectionColor = col;

            var lines = rtb.Lines;
            if (lines.Length > MAX_LINES)
                rtb.Text = string.Join("\n", lines.Skip(lines.Length - MAX_LINES));

            rtb.SelectionStart = rtb.TextLength;
            rtb.ScrollToCaret();
        }

        private Color GetColor(string cat)
        {
            if (cat == "ОШИБКИ") return Color.FromArgb(239, 68, 68);
            if (cat == "ДЕЙСТВИЯ") return Color.FromArgb(99, 102, 241);
            if (cat == "РЕАКЦИИ") return Color.FromArgb(34, 197, 94);
            if (cat == "РУМ") return Color.FromArgb(251, 191, 36);
            return Color.FromArgb(180, 180, 190);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            e.Cancel = true; Hide();
        }
    }

    internal sealed class ActiveTableInfo
    {
        [Newtonsoft.Json.JsonProperty("name")] public string Name { get; set; }
        [Newtonsoft.Json.JsonProperty("active")] public bool Active { get; set; }
        [Newtonsoft.Json.JsonProperty("secondsAgo")] public int SecondsAgo { get; set; }
        /// <summary>Время за столом в секундах (с первого хэнда в файле).</summary>
        [Newtonsoft.Json.JsonProperty("uptimeSec")] public long UptimeSec { get; set; }
        /// <summary>Лимит, например "$0.5/$1".</summary>
        [Newtonsoft.Json.JsonProperty("limit")] public string Limit { get; set; }
        /// <summary>Есть ли за столом хотя бы один фиш (по H2N цветовой метке).</summary>
        [Newtonsoft.Json.JsonProperty("fishy")] public bool Fishy { get; set; }
        /// <summary>Список ников за столом (из последнего хэнда).</summary>
        [Newtonsoft.Json.JsonProperty("players")] public List<string> Players { get; set; }
        /// <summary>True = стол из архива AllHandsDir (не живой InputDir файл).</summary>
        [Newtonsoft.Json.JsonProperty("isArchive")] public bool IsArchive { get; set; }
    }

    // ═══════════════════════════════════════════════════════
    //  POKER CONVERTER
    // ═══════════════════════════════════════════════════════
    public class PokerConverter
    {
        public string InputDir { get; set; } = "";
        public string OutputDir { get; set; } = "";
        /// <summary>Папка архива всех раздач (AppData\WinHK3\AllHands). Дублируем каждую конвертацию.</summary>
        public string AllHandsDir { get; set; } = "";

        private readonly HashSet<string> _processedHands = new HashSet<string>();
        private readonly string _dbFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "processed_hands_cs.json");
        private CancellationTokenSource _cts;
        private bool _isRunning = false;

        public bool IsRunning => _isRunning;

        public event Action<string> OnLog
        {
            add { }
            remove { }
        }
        private void RaiseLog(string msg) => System.Diagnostics.Debug.WriteLine($"[Converter] {msg}");
        public event Action<string> OnWindowRenamed;

        private static readonly (string Old, string New)[] Replacements = new[]
        {
            ("1WinPoker Hand", "PokerStars Hand"),
            (")6-max", "'6-max"),
            (") 2-max", "' 2-max"),
            (")2-max", "'2-max"),
            (")9-max", "'9-max"),
            (" $", ""),
            ("(", "($"),
            ("9-max Seat ", "9-max Seat #"),
            ("small blind", "small blind $"),
            ("blind", "blind $"),
            ("bets", "bets $"),
            ("Total pot", "Total pot $"),
            ("Rake", "Rake $"),
            ("collected $(" , "collected ("),
            ("posts small blind $$0.05", "posts small blind $0.05"),
            ("#", ""),
            ("PokerStars Hand", "PokerStars Hand #"),
            ("calls", "calls $"),
            ("raises", "raises $"),
            ("collected", "collected $"),
            ("]($", "]("),
            ("($big blinds)", "(big blinds)"),
            ("($didn't bet)", "(didn't bet)"),
            ("($button)", "(button)"),
            ("to", "to $"),
            ("(Real Money)", ""),
            (" $", ""),
            ("bets", "bets $"),
            ("before Flop", "before Flop"),
            ("the Turn", "the Turn"),
            ("to $Ace", "to Ace"),
            ("on the River", "on the River"),
            ("e to $", "e to"),
            ("o to $", "o to"),
            ("($Real Money)", ""),
            ("6-max Seat ", "6-max Seat #"),
            ("($0.01/0.02)", "($0.01/$0.02)"),
            ("($0.01/0.02 )", "($0.01/$0.02)"),
            ("($0.02/0.04)", "($0.02/$0.04)"),
            ("($0.02/0.04 )", "($0.02/$0.04)"),
            ("($0.05/0.10)", "($0.05/$0.10)"),
            ("($0.10/0.20)", "($0.10/$0.20)"),
            ("($0.15/0.30)", "($0.15/$0.30)"),
            ("($0.25/0.50)", "($0.25/$0.50)"),
            ("($0.50/1)", "($0.50/$1)"),
            ("($1/2)", "($1/$2)"),
            ("($2/4)", "($2/$4)"),
            ("($5/10)", "($5/$10)"),
            ("($10/20)", "($10/$20)"),
            ("bets $ ", "bets $"),
            ("posts big blind ", "posts big blind $"),
            ("posts small blind ", "posts small blind $"),
            ("posts straddle ", "posts straddle $"),
            ("calls ", "calls $"),
            ("$a pair", "a pair"),
            ("($two pair", "(two pair"),
            ("Hand # ", "Hand #"),
            ("collected ", "collected $"),
            ("$$", "$"),
            ("$ $", "$"),
            ("($a full house", "(a full house"),
            ("raises ", "raises $"),
            ("Total pot ", "Total pot $"),
            ("Total pot $ ", "Total pot $"),
            ("Rake", "Rake $"),
            ("($big blinds)", "(big blinds)"),
            ("($small blinds)", "(small blinds)"),
            ("$($", "($"),
            ("$$", "$"),
            ("$ $ ", "$"),
            ("($small blind)", "(small blind)"),
            ("($big blind)", "(big blind)"),
            ("Rake$ ", "Rake $"),
            ("$a straight", "a straight"),
            ("Rake $ ", "Rake $"),
            ("($button)", "(button)"),
            ("$ ", "$"),
            ("butto $n", "button"),
            ("to $tal", "to total"),
            ("$)", ")"),
            (" Seat ", "Seat #"),
            (") 6-max", "' 6-max"),
            ("'($", ""),
            ("$$", "$"),
            ("'($", "$"),
            (") 9-max", "' 9-max"),
            ("posts the ante ", "posts the ante $"),
            ("posts the ante $$", "posts the ante $"),
        };

        public void LoadCache()
        {
            try
            {
                if (File.Exists(_dbFile))
                {
                    var arr = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(_dbFile, Encoding.UTF8));
                    if (arr != null) foreach (var h in arr) _processedHands.Add(h);
                }
            }
            catch { }
        }

        public void SaveCache()
        {
            try { File.WriteAllText(_dbFile, JsonConvert.SerializeObject(_processedHands.ToList()), Encoding.UTF8); }
            catch { }
        }

        public List<(string Id, string Text)> SplitHands(string content)
        {
            var result = new List<(string, string)>();
            var parts = content.Split(new[] { "1WinPoker Hand" }, StringSplitOptions.None);
            foreach (var part in parts)
            {
                if (!part.Contains("*** SUMMARY ***")) continue;
                string full = "1WinPoker Hand" + part;
                var m = System.Text.RegularExpressions.Regex.Match(full, @"1WinPoker Hand #?(\d+)");
                if (m.Success) result.Add((m.Groups[1].Value, full.Trim()));
            }
            return result;
        }

        public string ExtractCleanName(string filename)
        {
            var m = System.Text.RegularExpressions.Regex.Match(filename, @"HH\d+\s+(.*?)\s+\(#");
            return m.Success ? m.Groups[1].Value.Trim() : filename.Replace(".txt", "").Trim();
        }

        public string ConvertHandText(string handText, string cleanTableName = null)
        {
            foreach (var (old, newVal) in Replacements)
                handText = handText.Replace(old, newVal);
            if (!string.IsNullOrEmpty(cleanTableName))
                handText = System.Text.RegularExpressions.Regex.Replace(
                    handText, @"Table '.*?'", $"Table '{cleanTableName}'", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
            return handText + "\n\n";
        }

        public void SyncWindowName(string filename)
        {
            try
            {
                string cleanName = ExtractCleanName(filename);
                if (cleanName.Length <= 2) return;
                const uint WM_SETTEXT2 = 0x000C;
                EnumWindows((hWnd, _) =>
                {
                    var sb = new StringBuilder(512);
                    GetWindowText(hWnd, sb, 512);
                    string title = sb.ToString();
                    if (title.Contains(cleanName) && title != cleanName &&
                        !title.Contains("Notepad") && !title.Contains("Блокнот") &&
                        !title.Contains("Code") && !title.Contains("Converter") && !title.Contains(".txt"))
                    {
                        SendMessage(hWnd, WM_SETTEXT2, IntPtr.Zero, cleanName);
                        OnWindowRenamed?.Invoke(cleanName);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        public void ScanAndRenameAllTableWindows()
        {
            const string pattern = @"^(.*?)\s+-\s+Холдем NL";
            const uint WM_SETTEXT2 = 0x000C;
            try
            {
                EnumWindows((hWnd, _) =>
                {
                    var sb = new StringBuilder(512);
                    GetWindowText(hWnd, sb, 512);
                    string title = sb.ToString();
                    var m = System.Text.RegularExpressions.Regex.Match(title, pattern);
                    if (m.Success)
                    {
                        string clean = m.Groups[1].Value.Trim();
                        if (title != clean)
                        {
                            SendMessage(hWnd, WM_SETTEXT2, IntPtr.Zero, clean);
                            OnWindowRenamed?.Invoke(clean);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        public void StartLive(Action<string> logCallback)
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            System.Threading.Tasks.Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (Directory.Exists(InputDir) && Directory.Exists(OutputDir))
                        {
                            foreach (var fpath in Directory.GetFiles(InputDir, "*.txt"))
                            {
                                string fname = Path.GetFileName(fpath);
                                try
                                {
                                    string content = File.ReadAllText(fpath, Encoding.UTF8);
                                    var hands = SplitHands(content);
                                    var newContent = new List<string>();
                                    string table = ExtractCleanName(fname);
                                    foreach (var (hid, htxt) in hands)
                                    {
                                        if (!_processedHands.Contains(hid))
                                        {
                                            _processedHands.Add(hid);
                                            newContent.Add(ConvertHandText(htxt, table));
                                        }
                                    }
                                    if (newContent.Count > 0)
                                    {
                                        SaveCache();
                                        string joined = string.Concat(newContent);
                                        File.AppendAllText(
                                            Path.Combine(OutputDir, fname),
                                            joined,
                                            Encoding.UTF8);
                                        // ── Дублируем в AllHands ──────────────────────────
                                        if (!string.IsNullOrEmpty(AllHandsDir) && Directory.Exists(AllHandsDir))
                                        {
                                            try
                                            {
                                                File.AppendAllText(
                                                    Path.Combine(AllHandsDir, fname),
                                                    joined,
                                                    Encoding.UTF8);
                                            }
                                            catch { }
                                        }
                                        logCallback?.Invoke($"Write: {table} (+{newContent.Count})");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    await System.Threading.Tasks.Task.Delay(1500, token).ContinueWith(_ => { });
                }
            });
        }

        public void StopLive()
        {
            _isRunning = false;
            _cts?.Cancel();
        }

        public int RunBatchConvert()
        {
            int cnt = 0;
            if (!Directory.Exists(InputDir) || !Directory.Exists(OutputDir)) return 0;
            foreach (var fpath in Directory.GetFiles(InputDir, "*.txt"))
            {
                string fname = Path.GetFileName(fpath);
                try
                {
                    string content = File.ReadAllText(fpath, Encoding.UTF8);
                    var hands = SplitHands(content);
                    string table = ExtractCleanName(fname);
                    var converted = hands.Select(h =>
                    {
                        _processedHands.Add(h.Id);
                        return ConvertHandText(h.Text, table);
                    }).ToList();
                    if (converted.Count > 0)
                    {
                        string joined = string.Concat(converted);
                        File.WriteAllText(Path.Combine(OutputDir, fname), joined, Encoding.UTF8);
                        // ── Дублируем в AllHands ───────────────────────────
                        if (!string.IsNullOrEmpty(AllHandsDir) && Directory.Exists(AllHandsDir))
                        {
                            try { File.WriteAllText(Path.Combine(AllHandsDir, fname), joined, Encoding.UTF8); }
                            catch { }
                        }
                        cnt++;
                    }
                }
                catch { }
            }
            SaveCache();
            return cnt;
        }
    }

    //  POINT OF ENTRY
    // ═══════════════════════════════════════════════════════
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }
    }
}