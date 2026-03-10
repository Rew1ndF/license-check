using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WinHK3
{
    // ═══════════════════════════════════════════════════
    //  Форма ввода ключа активации
    //  Показывается при первом запуске или при сбросе лицензии
    // ═══════════════════════════════════════════════════
    public class LicenseActivationForm : Form
    {
        private Label _lblTitle;
        private Label _lblHwid;
        private Label _lblHwidValue;
        private Label _lblKey;
        private TextBox _txtKey;
        private Button _btnActivate;
        private Button _btnCancel;
        private Label _lblStatus;
        private Panel _panelTop;

        // FIX CS0107: PlaceholderText недоступен в .NET 4.8 WinForms
        // Реализуем вручную через GotFocus/LostFocus
        private const string PLACEHOLDER = "Введите ключ активации...";

        public LicenseActivationForm()
        {
            InitUI();
        }

        private void InitUI()
        {
            this.Text = "Активация WinHK3";
            this.Size = new Size(420, 280);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(245, 245, 248);

            // ─── Верхняя полоса ──────────────────────
            _panelTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Color.FromArgb(30, 30, 46)
            };

            _lblTitle = new Label
            {
                Text = "🔐  Активация программы",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0)
            };
            _panelTop.Controls.Add(_lblTitle);

            // ─── HWID ─────────────────────────────────
            string hwid = LicenseManager.GetHwid();

            _lblHwid = new Label
            {
                Text = "ID вашего устройства:",
                Location = new Point(18, 68),
                AutoSize = true,
                ForeColor = Color.FromArgb(90, 90, 90),
                Font = new Font("Segoe UI", 9f)
            };

            _lblHwidValue = new Label
            {
                Text = hwid,
                Location = new Point(18, 86),
                AutoSize = true,
                ForeColor = Color.FromArgb(40, 40, 40),
                Font = new Font("Consolas", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            // Клик — копировать HWID в буфер
            _lblHwidValue.Click += (s, e) =>
            {
                Clipboard.SetText(hwid);
                _lblHwidValue.Text = "Скопировано!";
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (sender2, e2) => { _lblHwidValue.Text = hwid; t.Dispose(); };
                t.Start();
            };
            _lblHwidValue.MouseEnter += (s, e) => _lblHwidValue.ForeColor = Color.FromArgb(0, 120, 215);
            _lblHwidValue.MouseLeave += (s, e) => _lblHwidValue.ForeColor = Color.FromArgb(40, 40, 40);

            // ─── Ключ активации ───────────────────────
            _lblKey = new Label
            {
                Text = "Ключ активации:",
                Location = new Point(18, 118),
                AutoSize = true,
                ForeColor = Color.FromArgb(90, 90, 90),
                Font = new Font("Segoe UI", 9f)
            };

            _txtKey = new TextBox
            {
                Location = new Point(18, 136),
                Size = new Size(368, 28),
                Font = new Font("Consolas", 11f),
                BorderStyle = BorderStyle.FixedSingle,
                // FIX CS0117: PlaceholderText нет в .NET 4.8 — реализуем вручную ниже
                Text = PLACEHOLDER,
                ForeColor = Color.Gray,
                CharacterCasing = CharacterCasing.Upper
            };
            _txtKey.GotFocus += (s, e) =>
            {
                if (_txtKey.Text == PLACEHOLDER.ToUpper() || _txtKey.Text == PLACEHOLDER)
                {
                    _txtKey.Text = "";
                    _txtKey.ForeColor = Color.FromArgb(20, 20, 20);
                }
            };
            _txtKey.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_txtKey.Text))
                {
                    _txtKey.Text = PLACEHOLDER;
                    _txtKey.ForeColor = Color.Gray;
                }
            };
            _txtKey.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) DoActivate();
            };

            // ─── Статус ───────────────────────────────
            _lblStatus = new Label
            {
                Location = new Point(18, 172),
                Size = new Size(368, 20),
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.Gray,
                Text = ""
            };

            // ─── Кнопки ───────────────────────────────
            _btnActivate = new Button
            {
                Text = "Активировать",
                Location = new Point(200, 200),
                Size = new Size(130, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 30, 46),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnActivate.FlatAppearance.BorderSize = 0;
            _btnActivate.Click += (s, e) => DoActivate();

            _btnCancel = new Button
            {
                Text = "Отмена",
                Location = new Point(108, 200),
                Size = new Size(84, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(220, 220, 230),
                ForeColor = Color.FromArgb(60, 60, 60),
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand
            };
            _btnCancel.FlatAppearance.BorderSize = 0;
            _btnCancel.Click += (s, e) =>
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };

            // ─── Сборка формы ─────────────────────────
            this.Controls.AddRange(new Control[]
            {
                _panelTop, _lblHwid, _lblHwidValue, _lblKey,
                _txtKey, _lblStatus, _btnActivate, _btnCancel
            });
        }

        // FIX #8: DoActivate теперь async — убран Thread.Sleep(800) блокирующий UI-поток.
        // Вместо этого используем await Task.Delay(800) который не замораживает форму.
        // Кнопка подписана через async void (стандартный паттерн для WinForms event handler).
        private async void DoActivate()
        {
            string key = _txtKey.Text.Trim();
            if (string.IsNullOrEmpty(key) || key == PLACEHOLDER.ToUpper() || key == PLACEHOLDER)
            {
                SetStatus("⚠ Введите ключ активации", Color.OrangeRed);
                return;
            }

            _btnActivate.Enabled = false;
            _btnActivate.Text = "Проверка...";
            _btnCancel.Enabled = false;
            SetStatus("Проверяем лицензию...", Color.Gray);

            // FIX #13: сетевой вызов вынесен в Task.Run чтобы не блокировать UI-поток
            // даже при трёх повторных попытках по 10 сек каждая (итого до ~32 сек ожидания).
            bool ok = await Task.Run(() => LicenseManager.Activate(key));

            _btnActivate.Enabled = true;
            _btnActivate.Text = "Активировать";
            _btnCancel.Enabled = true;

            if (ok)
            {
                SetStatus("✓ Лицензия активирована!", Color.Green);
                // FIX #8: await Task.Delay вместо Thread.Sleep — UI не заморожен
                await Task.Delay(800);
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            else
            {
                var result = await Task.Run(() => LicenseManager.Validate(key));

                string msg;
                if (result.Status == LicenseStatus.InvalidKey)
                    msg = "✗ Неверный ключ активации";
                else if (result.Status == LicenseStatus.HwidMismatch)
                    msg = "✗ Ключ привязан к другому устройству";
                else if (result.Status == LicenseStatus.Expired)
                    msg = "✗ Лицензия истекла (" + (result.Expiry.HasValue ? result.Expiry.Value.ToString("dd.MM.yyyy") : "—") + ")";
                else if (result.Status == LicenseStatus.NetworkError)
                    msg = "✗ Нет соединения с интернетом";
                else if (result.Status == LicenseStatus.NotActivated)
                    msg = "⏳ Ожидайте привязки устройства...";
                else
                    msg = "✗ Ошибка активации";

                Color msgColor = result.Status == LicenseStatus.NotActivated ? Color.DarkOrange : Color.OrangeRed;
                SetStatus(msg, msgColor);
            }
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }
    }
}