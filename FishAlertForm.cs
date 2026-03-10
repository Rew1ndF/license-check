using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinHK3
{
    // ═══════════════════════════════════════════════════════
    //  FishAlertForm
    //  Небольшое полупрозрачное уведомление поверх стола.
    //  НЕ блокирует другие окна — WS_EX_NOACTIVATE + click-through нижней части.
    //  Кнопки кликабельны, фон — нет.
    // ═══════════════════════════════════════════════════════
    public class FishAlertForm : Form
    {
        // ── Win32 ────────────────────────────────────────────────
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int val);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint cr, byte alpha, uint flags);

        private const int GWL_EXSTYLE   = -20;
        private const int WS_EX_LAYERED   = 0x80000;
        private const int WS_EX_NOACTIVATE = 0x8000000;
        private const int WS_EX_TOPMOST    = 0x8;
        private const uint LWA_ALPHA       = 0x2;

        // ── Публичные события ─────────────────────────────────────
        public event Action OnSitOut;   // «Отойти на бб»
        public event Action OnStay;     // «Остаться»

        // ── Стол к которому привязано уведомление ─────────────────
        public string TableName { get; set; }
        public IntPtr TableHwnd { get; set; }

        // ── Контролы ─────────────────────────────────────────────
        private readonly Label  _lblMsg;
        private readonly Button _btnSitOut;
        private readonly Button _btnStay;
        private readonly Button _btnClose;

        // Размер окна
        private const int W = 260, H = 92;

        public FishAlertForm()
        {
            // Базовые свойства формы
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar   = false;
            TopMost         = true;
            StartPosition   = FormStartPosition.Manual;
            Size            = new Size(W, H);
            BackColor       = Color.FromArgb(18, 18, 30);   // фоновый цвет (будет полупрозрачным)
            TransparencyKey = Color.Empty;

            // ── Иконка + заголовок ────────────────────────────────
            _lblMsg = new Label
            {
                Text      = "🐟  Любителя нет",
                ForeColor = Color.FromArgb(250, 204, 21),
                Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                AutoSize  = false,
                Bounds    = new Rectangle(10, 12, W - 36, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
            };

            // ── Кнопка «Отойти на бб» ─────────────────────────────
            _btnSitOut = MakeBtn("Отойти на BB", new Rectangle(10, 42, 118, 28),
                Color.FromArgb(239, 68, 68), Color.White);
            _btnSitOut.Click += (s, e) => { OnSitOut?.Invoke(); SafeClose(); };

            // ── Кнопка «Остаться» ────────────────────────────────
            _btnStay = MakeBtn("Остаться", new Rectangle(136, 42, 114, 28),
                Color.FromArgb(34, 197, 94), Color.White);
            _btnStay.Click += (s, e) => { OnStay?.Invoke(); SafeClose(); };

            // ── Крестик (= остаться) ─────────────────────────────
            _btnClose = new Button
            {
                Text      = "✕",
                Bounds    = new Rectangle(W - 26, 4, 22, 18),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(180, 180, 180),
                Font      = new Font("Segoe UI", 7.5f),
                Cursor    = Cursors.Hand,
                TabStop   = false,
            };
            _btnClose.FlatAppearance.BorderSize = 0;
            _btnClose.Click += (s, e) => { OnStay?.Invoke(); SafeClose(); };

            Controls.AddRange(new Control[] { _lblMsg, _btnSitOut, _btnStay, _btnClose });

            // Скруглённые углы через Region (простой вариант)
            Region = System.Drawing.Region.FromHrgn(CreateRoundRectRgn(0, 0, W, H, 10, 10));
        }

        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
            int nWidthEllipse, int nHeightEllipse);

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 82% непрозрачность
            SetLayeredWindowAttributes(Handle, 0, 210, LWA_ALPHA);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Тонкая рамка жёлтого цвета
            using (var pen = new Pen(Color.FromArgb(250, 204, 21), 1.5f))
            {
                g.DrawRectangle(pen, 1, 1, W - 3, H - 3);
            }

            // Разделитель под текстом
            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), 1))
            {
                g.DrawLine(pen, 10, 38, W - 10, 38);
            }
        }

        // Позиционировать уведомление по центру стола
        public void PositionOverTable(Rectangle tableRect)
        {
            int x = tableRect.Left + (tableRect.Width  - W) / 2;
            int y = tableRect.Top  + (tableRect.Height - H) / 2 - 20; // чуть выше центра
            Location = new Point(x, y);
        }

        private void SafeClose()
        {
            if (!IsDisposed) { Hide(); Dispose(); }
        }

        private static Button MakeBtn(string text, Rectangle bounds, Color bg, Color fg)
        {
            var b = new Button
            {
                Text      = text,
                Bounds    = bounds,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = fg,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Cursor    = Cursors.Hand,
                TabStop   = false,
            };
            b.FlatAppearance.BorderSize = 0;
            // Hover эффект
            b.MouseEnter += (s, e) => b.BackColor = ControlPaint.Dark(bg, 0.1f);
            b.MouseLeave += (s, e) => b.BackColor = bg;
            return b;
        }
    }
}
