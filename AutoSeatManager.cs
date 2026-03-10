using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinHK3
{
    // ═══════════════════════════════════════════════════════════════════════
    //  AutoSeatManager — автопосадка слева от нового игрока (6-max, 1win)
    //
    //  Принцип работы:
    //  1. Каждые ScanIntervalMs делает скриншот стола через PrintWindow
    //  2. Сравнивает зоны seat'ов (аватар/никнейм) с предыдущим кадром
    //  3. Если diff > порога — на этом seat появился новый игрок
    //  4. Определяет ближайший свободный seat слева (против часовой)
    //  5. Кликает WM_LBUTTONDOWN/UP на кнопку "Change Seat" для этого seat
    //
    //  Интеграция в Form1:
    //    private readonly AutoSeatManager _autoSeat;   // поле класса
    //
    //    В конструкторе Form1():
    //    _autoSeat = new AutoSeatManager(
    //        getTableHwnd: () => GetPrimaryTableHwnd(),   // см. ниже
    //        silentClick: SilentClick,
    //        log: (msg) => AppLog("РУМ", msg));
    //
    //    Включение/выключение (из OnWebMessage, cmd="toggleAutoSeat"):
    //    if (_autoSeat.IsRunning) _autoSeat.Stop(); else _autoSeat.Start();
    //
    //  Метод GetPrimaryTableHwnd() — добавить в Form1:
    //    private IntPtr GetPrimaryTableHwnd() {
    //        IntPtr result = IntPtr.Zero;
    //        EnumWindows((h, _) => {
    //            var sb = new System.Text.StringBuilder(256);
    //            GetWindowText(h, sb, 256);
    //            if (IsTableWindow(sb.ToString())) { result = h; return false; }
    //            return true;
    //        }, IntPtr.Zero);
    //        return result;
    //    }
    // ═══════════════════════════════════════════════════════════════════════
    public class AutoSeatManager
    {
        // ── Настройки (можно менять снаружи до Start()) ──────────────────
        public int ScanIntervalMs = 500;   // частота сканирования
        public int DiffThreshold = 35;    // порог пиксельного diff (0-255)
        public int DiffMinPixels = 120;   // минимум «изменившихся» пикселей чтобы считать seat занятым
        public int ColorPct = 20;    // % цветных пикселей в зоне для признания seat занятым
        public int CooldownMs = 2500;  // антиспам кулдаун в мс
        public int ClickDelayMs = 120;   // задержка между кликами Change Seat
        public bool Enabled = true;  // мастер-переключатель

        // ── Методы обновления координат из UI ───────────────────────────
        public void SetSeatZone(int seat, double x, double y, double w, double h)
        {
            if (seat < 1 || seat > 6) return;
            SeatDetectZones[seat, 0] = x;
            SeatDetectZones[seat, 1] = y;
            SeatDetectZones[seat, 2] = w;
            SeatDetectZones[seat, 3] = h;
        }

        public void SetSeatClick(int seat, double x, double y)
        {
            if (seat < 1 || seat > 6) return;
            ChangeSeatClickX[seat] = x;
            ChangeSeatClickY[seat] = y;
        }

        // ── Публичное состояние ──────────────────────────────────────────
        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        // ── Колбэки из Form1 ─────────────────────────────────────────────
        private readonly Func<IntPtr> _getTableHwnd;
        private readonly Action<IntPtr, int, int> _silentClick;
        private readonly Action<string> _log;

        // ── Внутреннее состояние ─────────────────────────────────────────
        private CancellationTokenSource _cts;
        private Bitmap _prevFrame;
        private bool[] _prevOccupied = new bool[7]; // индексы 1-6
        private DateTime _lastTrigger = DateTime.MinValue;
        private const int COOLDOWN_MS = 2500; // антиспам: не более 1 действия за N мс

        // ── WinAPI (дублируем минимум, не трогая Form1) ──────────────────
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr ho);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr hdc);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        private const uint PW_CLIENTONLY = 0x1;
        private const uint PW_RENDERFULLCONTENT = 0x2;

        // ════════════════════════════════════════════════════════════════
        //  6-MAX SEAT LAYOUT (относительные координаты 0.0-1.0)
        // ════════════════════════════════════════════════════════════════

        private double[,] SeatDetectZones = new double[7, 4]
        {
            { 0, 0, 0, 0 },
            { 0.38, 0.78, 0.24, 0.14 },
            { 0.11, 0.61, 0.20, 0.14 },
            { 0.04, 0.34, 0.20, 0.14 },
            { 0.11, 0.08, 0.20, 0.14 },
            { 0.66, 0.08, 0.20, 0.14 },
            { 0.73, 0.34, 0.20, 0.14 },
        };

        private double[] ChangeSeatClickX = new double[]
        { 0, 0.50, 0.21, 0.14, 0.21, 0.76, 0.83 };
        private double[] ChangeSeatClickY = new double[]
        { 0, 0.85, 0.68, 0.41, 0.15, 0.15, 0.41 };

        // ─────────────────────────────────────────────────────────────────
        public AutoSeatManager(
            Func<IntPtr> getTableHwnd,
            Action<IntPtr, int, int> silentClick,
            Action<string> log)
        {
            _getTableHwnd = getTableHwnd;
            _silentClick = silentClick;
            _log = log;
        }

        // ─────────────────────────────────────────────────────────────────
        //  Start / Stop
        // ─────────────────────────────────────────────────────────────────
        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _prevFrame = null;
            Array.Clear(_prevOccupied, 0, _prevOccupied.Length);
            _log("[AutoSeat] Запущен. Интервал=" + ScanIntervalMs + "мс, порог=" + DiffThreshold);
            Task.Run(() => ScanLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _prevFrame?.Dispose();
            _prevFrame = null;
            _log("[AutoSeat] Остановлен.");
        }

        // ─────────────────────────────────────────────────────────────────
        //  Главный цикл
        // ─────────────────────────────────────────────────────────────────
        private async Task ScanLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (Enabled) Tick();
                }
                catch (Exception ex)
                {
                    _log("[AutoSeat] Ошибка: " + ex.Message);
                }

                try { await Task.Delay(ScanIntervalMs, token); }
                catch (TaskCanceledException) { break; }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Один тик сканирования
        // ─────────────────────────────────────────────────────────────────
        private void Tick()
        {
            IntPtr hwnd = _getTableHwnd();
            if (hwnd == IntPtr.Zero) return;

            // 1. Снимаем скриншот клиентской зоны стола
            Bitmap frame = CaptureWindow(hwnd);
            if (frame == null) return;

            int w = frame.Width, h = frame.Height;
            if (w < 100 || h < 100) { frame.Dispose(); return; }

            // 2. Определяем занятость каждого seat по текущему кадру
            bool[] nowOccupied = DetectOccupiedSeats(frame, w, h);

            // 3. Если предыдущий кадр есть — ищем новых игроков
            if (_prevFrame != null && _prevFrame.Width == w && _prevFrame.Height == h)
            {
                for (int seat = 1; seat <= 6; seat++)
                {
                    // Seat стал занят (раньше был свободен) — новый игрок!
                    if (nowOccupied[seat] && !_prevOccupied[seat])
                    {
                        _log($"[AutoSeat] Новый игрок на Seat {seat}!");
                        TryAutoSeat(hwnd, seat, nowOccupied, w, h);
                    }
                }
            }

            // 4. Обновляем состояние
            _prevFrame?.Dispose();
            _prevFrame = frame;
            _prevOccupied = nowOccupied;
        }

        // ─────────────────────────────────────────────────────────────────
        //  Детекция занятых мест по текущему кадру
        //
        //  Использует пиксельный анализ зоны аватара:
        //  Если зона содержит достаточно «цветных» (не серых/тёмных) пикселей —
        //  seat занят. Пустые места в 1win имеют тёмный/серый placeholder.
        // ─────────────────────────────────────────────────────────────────
        private bool[] DetectOccupiedSeats(Bitmap bmp, int w, int h)
        {
            var occupied = new bool[7];

            // Копируем все пиксели через Marshal.Copy — не требует /unsafe
            BitmapData data = bmp.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            int stride = data.Stride;
            int byteCount = Math.Abs(stride) * h;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);
            bmp.UnlockBits(data);

            for (int seat = 1; seat <= 6; seat++)
            {
                double rx = SeatDetectZones[seat, 0];
                double ry = SeatDetectZones[seat, 1];
                double rw = SeatDetectZones[seat, 2];
                double rh = SeatDetectZones[seat, 3];

                int x0 = Math.Max(0, (int)(rx * w));
                int y0 = Math.Max(0, (int)(ry * h));
                int x1 = Math.Min(w - 1, (int)((rx + rw) * w));
                int y1 = Math.Min(h - 1, (int)((ry + rh) * h));

                int coloredPixels = 0;
                int totalPixels = 0;

                for (int y = y0; y <= y1; y += 2)
                {
                    for (int x = x0; x <= x1; x += 2)
                    {
                        int idx = y * stride + x * 4;
                        byte b = pixels[idx];
                        byte g = pixels[idx + 1];
                        byte r = pixels[idx + 2];

                        // Цветной пиксель = высокая насыщенность или яркость
                        // Пустое место в 1win — тёмно-серый или тёмно-зелёный фон
                        int maxC = Math.Max(r, Math.Max(g, b));
                        int minC = Math.Min(r, Math.Min(g, b));
                        int sat = maxC - minC; // хроматическая насыщенность
                        bool isColored = (sat > 40) || (maxC > 180);

                        if (isColored) coloredPixels++;
                        totalPixels++;
                    }
                }

                // Занят если >20% зоны «цветные»
                occupied[seat] = totalPixels > 0 &&
                                 (coloredPixels * 100 / totalPixels) > 20;
            }

            return occupied;
        }

        // ─────────────────────────────────────────────────────────────────
        //  Логика выбора seat'а и клика
        // ─────────────────────────────────────────────────────────────────
        private void TryAutoSeat(IntPtr hwnd, int newPlayerSeat, bool[] occupied, int w, int h)
        {
            // Антиспам
            if ((DateTime.Now - _lastTrigger).TotalMilliseconds < COOLDOWN_MS)
            {
                _log($"[AutoSeat] Кулдаун активен, пропускаем.");
                return;
            }

            // Ищем ближайший свободный seat СЛЕВА (против часовой стрелки = уменьшаем номер)
            // В 6-max: "левее" = seat с меньшим номером, но циклически (1 левее 6 → это 5, 4...)
            // Порядок обхода: newSeat-1, newSeat-2, ... (по кругу)
            int targetSeat = FindFreeSeatLeft(newPlayerSeat, occupied);

            if (targetSeat < 0)
            {
                _log($"[AutoSeat] Нет свободных мест слева от Seat {newPlayerSeat}.");
                return;
            }

            _log($"[AutoSeat] Новый игрок: Seat {newPlayerSeat} → садимся на Seat {targetSeat}");
            _lastTrigger = DateTime.Now;

            // Кликаем на кнопку "Сесть" для targetSeat
            Thread.Sleep(ClickDelayMs); // небольшая задержка для надёжности

            int cx = (int)(ChangeSeatClickX[targetSeat] * w);
            int cy = (int)(ChangeSeatClickY[targetSeat] * h);
            _silentClick(hwnd, cx, cy);

            _log($"[AutoSeat] Клик: Seat {targetSeat} @ ({cx},{cy}) в окне {hwnd}");

            // Второй клик через 200мс — на случай если 1win требует подтверждения
            Thread.Sleep(200);
            _silentClick(hwnd, cx, cy);
        }

        // ─────────────────────────────────────────────────────────────────
        //  Поиск ближайшего свободного seat слева (против часовой стрелки)
        //  В 6-max seat'ы нумеруются 1-6 по часовой стрелке:
        //  1(низ-право) → 2(низ-лево) → 3(лево) → 4(верх-лево) → 5(верх-право) → 6(право)
        //  "Слева" от seat X = seat X-1 (циклически), то есть
        //  слева от 1 → 6 → 5 → 4... и т.д.
        // ─────────────────────────────────────────────────────────────────
        private static int FindFreeSeatLeft(int fromSeat, bool[] occupied)
        {
            for (int i = 1; i < 6; i++)
            {
                // Идём по часовой стрелке назад: 6,5,4,3,2,1 циклически
                int candidate = ((fromSeat - 1 - i + 6) % 6) + 1;
                if (!occupied[candidate])
                    return candidate;
            }
            return -1; // все места заняты
        }

        // ─────────────────────────────────────────────────────────────────
        //  Скриншот клиентской зоны окна через PrintWindow
        //  (работает для фонового/перекрытого окна — как в Form1)
        // ─────────────────────────────────────────────────────────────────
        private static Bitmap CaptureWindow(IntPtr hwnd)
        {
            try
            {
                GetClientRect(hwnd, out RECT client);
                int w = client.Right - client.Left;
                int h = client.Bottom - client.Top;
                if (w <= 0 || h <= 0) return null;

                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hBmp = CreateCompatibleBitmap(screenDc, w, h);
                IntPtr hOld = SelectObject(memDc, hBmp);

                bool ok = PrintWindow(hwnd, memDc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
                if (!ok) PrintWindow(hwnd, memDc, PW_CLIENTONLY); // fallback

                Bitmap bmp = null;
                try { bmp = Image.FromHbitmap(hBmp); }
                catch { /* редко, но Image.FromHbitmap может бросить */ }

                SelectObject(memDc, hOld);
                DeleteDC(memDc);
                DeleteObject(hBmp);
                ReleaseDC(IntPtr.Zero, screenDc);

                return bmp;
            }
            catch { return null; }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  AutoSeatCalibrator — вспомогательный инструмент калибровки
    //
    //  Сохраняет скриншот стола с нанесёнными зонами seat'ов в файл.
    //  Вызывать один раз для проверки правильности координат:
    //
    //    AutoSeatCalibrator.SaveDebugImage(hwnd, @"C:\debug_seats.png");
    //
    //  Смотришь картинку — если прямоугольники не совпадают с аватарами,
    //  правишь константы SeatDetectZones в AutoSeatManager выше.
    // ═══════════════════════════════════════════════════════════════════════
    public static class AutoSeatCalibrator
    {
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out AutoSeatManager_RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr ho);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct AutoSeatManager_RECT { public int Left, Top, Right, Bottom; }

        private static readonly double[,] Zones = AutoSeatManager_GetZones();

        // Рефлексия не нужна — просто дублируем координаты для калибратора
        private static double[,] AutoSeatManager_GetZones() => new double[7, 4]
        {
            { 0,    0,    0,    0    },
            { 0.38, 0.78, 0.24, 0.14 },
            { 0.11, 0.61, 0.20, 0.14 },
            { 0.04, 0.34, 0.20, 0.14 },
            { 0.11, 0.08, 0.20, 0.14 },
            { 0.66, 0.08, 0.20, 0.14 },
            { 0.73, 0.34, 0.20, 0.14 },
        };

        private static readonly double[] ClickX = { 0, 0.50, 0.21, 0.14, 0.21, 0.76, 0.83 };
        private static readonly double[] ClickY = { 0, 0.85, 0.68, 0.41, 0.15, 0.15, 0.41 };

        public static void SaveDebugImage(IntPtr hwnd, string outputPath)
        {
            try
            {
                GetClientRect(hwnd, out AutoSeatManager_RECT client);
                int w = client.Right, h = client.Bottom;
                if (w <= 0 || h <= 0) return;

                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hBmp = CreateCompatibleBitmap(screenDc, w, h);
                IntPtr hOld = SelectObject(memDc, hBmp);
                PrintWindow(hwnd, memDc, 0x1 | 0x2);
                Bitmap bmp;
                try { bmp = Image.FromHbitmap(hBmp); }
                catch { SelectObject(memDc, hOld); DeleteDC(memDc); DeleteObject(hBmp); ReleaseDC(IntPtr.Zero, screenDc); return; }
                SelectObject(memDc, hOld);
                DeleteDC(memDc); DeleteObject(hBmp); ReleaseDC(IntPtr.Zero, screenDc);

                using (var g = Graphics.FromImage(bmp))
                {
                    var colors = new[] {
                        Color.Red, Color.Lime, Color.Cyan,
                        Color.Yellow, Color.Magenta, Color.Orange
                    };

                    for (int s = 1; s <= 6; s++)
                    {
                        var c = colors[s - 1];
                        var pen = new Pen(c, 2);
                        int x0 = (int)(Zones[s, 0] * w);
                        int y0 = (int)(Zones[s, 1] * h);
                        int rw = (int)(Zones[s, 2] * w);
                        int rh = (int)(Zones[s, 3] * h);
                        g.DrawRectangle(pen, x0, y0, rw, rh);

                        // Точка клика Change Seat
                        int cx = (int)(ClickX[s] * w);
                        int cy = (int)(ClickY[s] * h);
                        g.FillEllipse(new SolidBrush(Color.FromArgb(200, c)), cx - 8, cy - 8, 16, 16);
                        g.DrawString($"S{s}", new Font("Arial", 10, FontStyle.Bold),
                            new SolidBrush(c), x0, y0 - 15);
                    }
                }

                bmp.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
                bmp.Dispose();
            }
            catch { }
        }
    }
}