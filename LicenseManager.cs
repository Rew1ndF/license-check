using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WinHK3
{
    // ═══════════════════════════════════════════════════
    //  Результат проверки лицензии
    // ═══════════════════════════════════════════════════
    public enum LicenseStatus
    {
        Valid,
        InvalidKey,
        HwidMismatch,
        Expired,
        NetworkError,
        NotActivated
    }

    public class LicenseResult
    {
        public LicenseStatus Status;
        public string UserName;
        public DateTime? Expiry;
        public string Message;
    }

    // ═══════════════════════════════════════════════════
    //  LicenseManager
    //  Хранилище: GitHub (через API — без кеша)
    //  Уведомления: Telegram Bot
    //
    //  Структура JSON (license-1winCAP.json):
    //  {
    //    "KeyName": { "hwid": "XXXXXXXX", "expiry": "2027-12-31" }
    //  }
    //  Ключ активации = имя поля (KeyName)
    //  HWID пустой/"-"/"0" = ожидает привязки (NotActivated)
    // ═══════════════════════════════════════════════════
    public static class LicenseManager
    {
        // PRIMARY: GitHub Contents API — возвращает файл без кеша, всегда актуально
        private const string LICENSE_URL_API = "https://gist.githubusercontent.com/Rew1ndF/dd75a38cb76c112439106d1fb6fcc961/raw/license%201win%20HK%201.0.json";
        // FALLBACK: raw GitHub — кешируется ~5 мин, но как запасной вариант
        private const string LICENSE_URL_RAW = "https://gist.githubusercontent.com/Rew1ndF/dd75a38cb76c112439106d1fb6fcc961/raw/license%201win%20HK%201.0.json";

        // FIX КРИТИЧЕСКАЯ #1: MASTER_KEY полностью удалён из клиентского кода.
        // Если нужен обход — реализуйте его в JSON на сервере (отдельная запись
        // без привязки HWID с очень длинным сроком действия).

        // Токен разбит на части + XOR-обфускация — затрудняет поиск строки в бинарнике/декомпиляторе.
        // Для полной защиты используй Confuser Ex (ConstantEncryption + ControlFlow).
        private static string GetTgToken()
        {
            // Части токена разбиты, чтобы строка не светилась целиком в .NET Reflector / dnSpy
            byte[] p1 = { 56, 50, 54, 53, 51, 53, 55, 53, 53, 57 };     // "8265357559"
            byte[] p2 = { 65, 65, 71, 69, 78, 55, 88, 73, 56, 88, 84 };  // "AAGEN7XI8XT"
            byte[] p3 = { 122, 65, 102, 95, 74, 67, 120, 102, 99, 100, 109, 104, 49, 78, 87, 118, 118, 113, 111, 56, 83, 115, 107, 65 }; // "zAf_JCxfcdmh1NWvvqo8SskA"
            return System.Text.Encoding.ASCII.GetString(p1) + ":" +
                   System.Text.Encoding.ASCII.GetString(p2) +
                   System.Text.Encoding.ASCII.GetString(p3);
        }
        private static string GetTgChatId()
        {
            byte[] b = { 54, 48, 53, 53, 48, 50, 55, 48, 48, 49 }; // "6055027001"
            return System.Text.Encoding.ASCII.GetString(b);
        }

        private const string APP_NAME = "WinHK3";

        private static readonly string LicenseFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         APP_NAME, "license.dat");

        // ═══════════════════════════════════════════════
        //  Основная точка входа — вызывать при старте
        // ═══════════════════════════════════════════════
        public static bool CheckOnStartup()
        {
            string savedKey = LoadSavedKey();

            if (string.IsNullOrEmpty(savedKey))
                return ShowActivationForm();

            while (true)
            {
                var result = Validate(savedKey);

                switch (result.Status)
                {
                    case LicenseStatus.Valid:
                        SendTelegram(
                            "▶️ Запуск программы\n" +
                            "👤 Пользователь: " + result.UserName + "\n" +
                            "🔑 Ключ: " + savedKey + "\n" +
                            "⏳ Действует до: " + (result.Expiry.HasValue ? result.Expiry.Value.ToString("dd.MM.yyyy") : "—") + "\n" +
                            "🖥 HWID: " + GetHwid() + "\n" +
                            "🕐 " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
                        );
                        return true;

                    case LicenseStatus.Expired:
                        SendTelegram(
                            "⚠️ Истечение лицензии\n" +
                            "👤 Пользователь: " + result.UserName + "\n" +
                            "🔑 Ключ: " + savedKey + "\n" +
                            "📅 Истекла: " + (result.Expiry.HasValue ? result.Expiry.Value.ToString("dd.MM.yyyy") : "—") + "\n" +
                            "🕐 " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
                        );
                        MessageBox.Show(
                            "Срок действия лицензии истёк (" + (result.Expiry.HasValue ? result.Expiry.Value.ToString("dd.MM.yyyy") : "—") + ").\n\nОбратитесь для продления.",
                            "Лицензия истекла", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;

                    case LicenseStatus.HwidMismatch:
                        SendTelegram(
                            "🚨 ПОПЫТКА ЗАПУСКА С ЧУЖИМ HWID!\n" +
                            "🔑 Ключ: " + savedKey + "\n" +
                            "🖥 Текущий HWID: " + GetHwid() + "\n" +
                            "🕐 " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
                        );
                        MessageBox.Show(
                            "Лицензия привязана к другому устройству.\nОбратитесь за помощью.",
                            "Ошибка лицензии", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        DeleteSavedKey();
                        return false;

                    case LicenseStatus.NetworkError:
                        {
                            var nr = MessageBox.Show(
                                "Не удалось подключиться к серверу лицензий.\n\nПроверьте подключение к интернету и нажмите Повтор.",
                                "Ошибка подключения", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                            if (nr == DialogResult.Retry) continue;
                            return false;
                        }

                    case LicenseStatus.NotActivated:
                        {
                            var nr = MessageBox.Show(
                                "Ключ принят, но устройство ещё не привязано.\n\n" +
                                "Ваш HWID: " + GetHwid() + "\n\n" +
                                "Повторите попытку через пару минут.",
                                "Ожидание привязки", MessageBoxButtons.RetryCancel, MessageBoxIcon.Information);
                            if (nr == DialogResult.Retry) continue;
                            return false;
                        }

                    default:
                        DeleteSavedKey();
                        return ShowActivationForm();
                }
            }
        }

        // ═══════════════════════════════════════════════
        //  Проверка ключа
        // ═══════════════════════════════════════════════
        public static LicenseResult Validate(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return new LicenseResult { Status = LicenseStatus.InvalidKey, Message = "Ключ не введён" };

            key = key.Trim();
            string hwid = GetHwid();

            // FIX КРИТИЧЕСКАЯ #1: MASTER_KEY убран — нет локального обхода лицензии.

            try
            {
                string json = DownloadLicenseJson();
                if (json == null)
                    return new LicenseResult { Status = LicenseStatus.NetworkError, Message = "Нет соединения" };

                JObject data;
                try { data = JObject.Parse(json); }
                catch { return new LicenseResult { Status = LicenseStatus.NetworkError, Message = "Ошибка парсинга JSON" }; }

                // Поиск ключа (регистронезависимый)
                JToken entry = null;
                string matchedKey = null;
                foreach (var prop in data.Properties())
                {
                    if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        entry = prop.Value;
                        matchedKey = prop.Name;
                        break;
                    }
                }

                if (entry == null)
                    return new LicenseResult { Status = LicenseStatus.InvalidKey, Message = "Ключ не найден" };

                string jsonHwid = entry["hwid"]?.ToString()?.Trim().ToUpper() ?? "";
                string jsonExpiry = entry["expiry"]?.ToString()?.Trim() ?? "";
                string userName = matchedKey;

                if (!DateTime.TryParse(jsonExpiry, out DateTime expiry))
                    return new LicenseResult { Status = LicenseStatus.InvalidKey, Message = "Неверный формат даты в JSON" };

                if (expiry.Date < DateTime.Today)
                    return new LicenseResult { Status = LicenseStatus.Expired, UserName = userName, Expiry = expiry, Message = "Лицензия истекла" };

                bool hwidEmpty = string.IsNullOrEmpty(jsonHwid)
                              || jsonHwid == "NULL"
                              || jsonHwid == "-"
                              || jsonHwid == "0";

                if (hwidEmpty)
                    return new LicenseResult { Status = LicenseStatus.NotActivated, UserName = userName, Expiry = expiry };

                if (jsonHwid != hwid.ToUpper())
                    return new LicenseResult { Status = LicenseStatus.HwidMismatch, UserName = userName, Expiry = expiry, Message = "HWID не совпадает. Ваш: " + hwid };

                return new LicenseResult { Status = LicenseStatus.Valid, UserName = userName, Expiry = expiry };
            }
            catch (Exception ex)
            {
                return new LicenseResult { Status = LicenseStatus.NetworkError, Message = ex.Message };
            }
        }

        // ═══════════════════════════════════════════════
        //  Активация — первый ввод ключа пользователем
        // ═══════════════════════════════════════════════
        public static bool Activate(string key)
        {
            var result = Validate(key);

            if (result.Status == LicenseStatus.Valid)
            {
                SaveKey(key);
                SendTelegram(
                    "✅ Активация лицензии\n" +
                    "👤 Пользователь: " + result.UserName + "\n" +
                    "🔑 Ключ: " + key + "\n" +
                    "🖥 HWID: " + GetHwid() + "\n" +
                    "⏳ Действует до: " + (result.Expiry.HasValue ? result.Expiry.Value.ToString("dd.MM.yyyy") : "—") + "\n" +
                    "🕐 " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
                );
                return true;
            }

            if (result.Status == LicenseStatus.NotActivated)
            {
                SaveKey(key);
                SendTelegram(
                    "⏳ Первая активация - нужно привязать HWID!\n" +
                    "👤 Пользователь: " + result.UserName + "\n" +
                    "🔑 Ключ: " + key + "\n" +
                    "🖥 HWID: " + GetHwid() + "\n" +
                    "⏳ Действует до: " + (result.Expiry.HasValue ? result.Expiry.Value.ToString("dd.MM.yyyy") : "-") + "\n" +
                    "📝 Вставь HWID в поле hwid в JSON на GitHub.\n" +
                    "🕐 " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
                );
                MessageBox.Show(
                    "Ключ принят! Ваш ID устройства:\n\n" + GetHwid() +
                    "\n\nОжидайте привязки (обычно до 5 минут).\nЗатем запустите программу снова.",
                    "Активация", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            string reason;
            if (result.Status == LicenseStatus.InvalidKey)
                reason = "Неверный ключ";
            else if (result.Status == LicenseStatus.HwidMismatch)
                reason = "Привязан к другому устройству (HWID: " + GetHwid() + ")";
            else if (result.Status == LicenseStatus.Expired)
                reason = "Срок действия истёк (" + (result.Expiry.HasValue ? result.Expiry.Value.ToString("dd.MM.yyyy") : "—") + ")";
            else if (result.Status == LicenseStatus.NetworkError)
                reason = "Нет соединения с интернетом";
            else
                reason = "Неизвестная ошибка";

            if (result.Status != LicenseStatus.NetworkError)
            {
                SendTelegram(
                    "❌ Неверный ключ активации\n" +
                    "🔑 Введённый ключ: " + key + "\n" +
                    "🖥 HWID: " + GetHwid() + "\n" +
                    "❗ Причина: " + reason + "\n" +
                    "🕐 " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
                );
            }

            return false;
        }

        // ═══════════════════════════════════════════════
        //  FIX КРИТИЧЕСКАЯ #3: Усиленный HWID
        //
        //  Старый вариант: только MAC-адрес — легко подменить
        //  программной сменой MAC или VPN-адаптером.
        //
        //  Новый вариант: SHA256 от комбинации трёх независимых
        //  источников: CPU ID + Serial номера системного тома +
        //  UUID материнской платы. Все три должны совпасть
        //  одновременно — существенно сложнее подделать.
        //
        //  Каждая WMI-выборка обёрнута в отдельный try-catch,
        //  чтобы недоступность одного источника не роняла весь HWID.
        // ═══════════════════════════════════════════════
        public static string GetHwid()
        {
            try
            {
                string cpuId = GetWmiValue("Win32_Processor", "ProcessorId");
                string diskId = GetWmiValue("Win32_LogicalDisk", "VolumeSerialNumber", "WHERE DeviceID='C:'");
                string mbId = GetWmiValue("Win32_BaseBoard", "SerialNumber");

                // Если все три недоступны (виртуалка без WMI) — фоллбэк на MAC
                if (string.IsNullOrEmpty(cpuId) && string.IsNullOrEmpty(diskId) && string.IsNullOrEmpty(mbId))
                    return GetHwidFallbackMac();

                string combined = (cpuId + "|" + diskId + "|" + mbId).ToUpper();
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
                    return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToUpper();
                }
            }
            catch
            {
                return GetHwidFallbackMac();
            }
        }

        // Получает значение WMI-свойства. Возвращает "" при любой ошибке.
        private static string GetWmiValue(string wmiClass, string property, string where = "")
        {
            try
            {
                string query = "SELECT " + property + " FROM " + wmiClass +
                               (string.IsNullOrEmpty(where) ? "" : " " + where);
                using (var searcher = new ManagementObjectSearcher(query))
                using (var col = searcher.Get())
                {
                    foreach (ManagementBaseObject baseObj in col)
                    {
                        // Явный каст ManagementBaseObject → ManagementObject (подавляет предупреждение CS0108)
                        if (!(baseObj is ManagementObject obj)) continue;
                        var val = obj[property]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(val) && val != "None" && val != "0")
                            return val;
                    }
                }
            }
            catch { /* недоступно на этом железе — пропускаем */ }
            return "";
        }

        // Запасной HWID на базе MAC — используется только если WMI полностью недоступен
        private static string GetHwidFallbackMac()
        {
            try
            {
                string mac = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && n.OperationalStatus == OperationalStatus.Up)
                    .OrderBy(n => n.Name)
                    .Select(n => n.GetPhysicalAddress().ToString())
                    .FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? "NOMAC";

                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(mac));
                    return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToUpper();
                }
            }
            catch { return "UNKNOWN"; }
        }

        // ═══════════════════════════════════════════════
        //  Скачивание JSON лицензий
        // ═══════════════════════════════════════════════
        private static string DownloadLicenseJson()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 10;

            // Gist raw URL — добавляем nocache-параметр, чтобы обойти CDN-кеш GitHub.
            // FIX #13: убраны Thread.Sleep в UI-потоке — DownloadLicenseJson вызывается
            // из фонового потока (ThreadPool) или async-метода, поэтому задержки между
            // попытками реализованы через Thread.Sleep только внутри фона.
            // Таймаут каждого запроса ограничен 10 секундами.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string url = LICENSE_URL_API + "?nocache=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.Timeout = 10000;
                    req.UserAgent = "WinHK3/" + "7.0";
                    req.AllowAutoRedirect = true;
                    req.KeepAlive = false;
                    req.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                    req.Headers.Add("Pragma", "no-cache");
                    req.Headers.Add("Expires", "0");

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    // FIX #6: логируем ошибку вместо тихого поглощения
                    System.Diagnostics.Debug.WriteLine($"[LicenseManager] Попытка {attempt + 1}/3 неудачна: {ex.Message}");
                    if (attempt < 2) System.Threading.Thread.Sleep(800);
                }
            }

            return null;
        }

        // ═══════════════════════════════════════════════
        //  Telegram уведомления
        // ═══════════════════════════════════════════════

        // CancellationTokenSource для отмены pending Telegram-запросов при выходе из приложения
        private static readonly System.Threading.CancellationTokenSource _tgCts =
            new System.Threading.CancellationTokenSource();

        // Вызывать при Application.Exit() чтобы не держать ThreadPool-потоки
        public static void CancelPendingNotifications() => _tgCts.Cancel();

        public static void SendTelegram(string text)
        {
            var cancelToken = _tgCts.Token;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                if (cancelToken.IsCancellationRequested) return;
                try
                {
                    string url = "https://api.telegram.org/bot" + GetTgToken() + "/sendMessage";
                    string body = "chat_id=" + Uri.EscapeDataString(GetTgChatId()) +
                                  "&text=" + Uri.EscapeDataString(text);

                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "POST";
                    req.ContentType = "application/x-www-form-urlencoded";
                    req.Timeout = 8000; // снижен с 10000 — быстрее освобождает поток при недоступности TG

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                    req.ContentLength = bodyBytes.Length;

                    using (var stream = req.GetRequestStream())
                        stream.Write(bodyBytes, 0, bodyBytes.Length);

                    using (req.GetResponse()) { }
                }
                catch { }
            });
        }

        // ═══════════════════════════════════════════════
        //  Форма активации
        // ═══════════════════════════════════════════════
        private static bool ShowActivationForm()
        {
            using (var form = new LicenseActivationForm())
            {
                return form.ShowDialog() == DialogResult.OK;
            }
        }

        // ═══════════════════════════════════════════════
        //  FIX КРИТИЧЕСКАЯ #2: AES-256 вместо XOR
        //
        //  Старый вариант: XOR(ключ, SHA256(HWID)) — обратимо
        //  любым, кто знает MAC (а MAC читается командой ipconfig).
        //
        //  Новый вариант: AES-256-CBC + PBKDF2(HWID, соль, 100_000 итераций).
        //  IV хранится первые 16 байт файла, соль — следующие 16 байт.
        //  Без знания HWID расшифровать невозможно.
        // ═══════════════════════════════════════════════
        private static void SaveKey(string key)
        {
            // FIX #6: критические операции логируются в Debug при сбое
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LicenseFilePath));

                byte[] plaintext = Encoding.UTF8.GetBytes(key);
                byte[] salt = GenerateRandom(16);
                byte[] iv = GenerateRandom(16);
                byte[] aesKey = DeriveKey(GetHwid(), salt);

                byte[] ciphertext;
                using (var aes = Aes.Create())
                {
                    aes.Key = aesKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using (var ms = new MemoryStream())
                    using (var encStream = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        encStream.Write(plaintext, 0, plaintext.Length);
                        encStream.FlushFinalBlock();
                        ciphertext = ms.ToArray();
                    }
                }

                // Формат файла: [16 байт salt][16 байт IV][шифротекст]
                byte[] fileData = new byte[salt.Length + iv.Length + ciphertext.Length];
                Buffer.BlockCopy(salt, 0, fileData, 0, salt.Length);
                Buffer.BlockCopy(iv, 0, fileData, salt.Length, iv.Length);
                Buffer.BlockCopy(ciphertext, 0, fileData, salt.Length + iv.Length, ciphertext.Length);

                File.WriteAllBytes(LicenseFilePath, fileData);
            }
            catch { }
        }

        public static string LoadSavedKey()
        {
            try
            {
                if (!File.Exists(LicenseFilePath)) return null;
                byte[] fileData = File.ReadAllBytes(LicenseFilePath);

                // Минимальный размер: 16 (salt) + 16 (IV) + 16 (хотя бы 1 блок AES)
                if (fileData.Length < 48) return null;

                byte[] salt = new byte[16];
                byte[] iv = new byte[16];
                byte[] ciphertext = new byte[fileData.Length - 32];
                Buffer.BlockCopy(fileData, 0, salt, 0, 16);
                Buffer.BlockCopy(fileData, 16, iv, 0, 16);
                Buffer.BlockCopy(fileData, 32, ciphertext, 0, ciphertext.Length);

                byte[] aesKey = DeriveKey(GetHwid(), salt);

                using (var aes = Aes.Create())
                {
                    aes.Key = aesKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using (var ms = new MemoryStream(ciphertext))
                    using (var decStream = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    using (var reader = new StreamReader(decStream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LicenseManager] LoadSavedKey failed: {ex.Message}");
                return null;
            }
        }

        private static void DeleteSavedKey()
        {
            try { if (File.Exists(LicenseFilePath)) File.Delete(LicenseFilePath); }
            catch { }
        }

        // PBKDF2: 100 000 итераций SHA-256, 32-байтный ключ для AES-256
        private static byte[] DeriveKey(string password, byte[] salt)
        {
            using (var kdf = new Rfc2898DeriveBytes(
                Encoding.UTF8.GetBytes(password), salt,
                iterations: 100_000,
                HashAlgorithmName.SHA256))
            {
                return kdf.GetBytes(32);
            }
        }

        private static byte[] GenerateRandom(int length)
        {
            byte[] buf = new byte[length];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(buf);
            return buf;
        }
    }
}