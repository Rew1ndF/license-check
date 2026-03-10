using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace WinHK3
{
    public class FishMonitor
    {
        public int HandsThreshold { get; set; } = 2;
        public int SnoozeMinutes { get; set; } = 4;

        private readonly H2NColorNoteReader _h2nReader;

        private class TableState
        {
            public string LastHandId = null;  // null = не инициализировано
            public int NoFishStreak = 0;
            public bool AlertShown = false;
            public DateTime SnoozedUntil = DateTime.MinValue;
        }

        private readonly Dictionary<string, TableState> _states
            = new Dictionary<string, TableState>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        /// <summary>
        /// Вызывается из background-потока при достижении порога.
        /// Диспатчить в UI через Form.BeginInvoke.
        /// </summary>
        public event Action<string> OnNoFishAlert;

        public FishMonitor(H2NColorNoteReader h2nReader)
        {
            _h2nReader = h2nReader ?? throw new ArgumentNullException(nameof(h2nReader));
        }

        // ─────────────────────────────────────────────────────────────────
        //  Основной метод — вызывать при каждом тике для каждого активного стола
        // ─────────────────────────────────────────────────────────────────
        public void CheckFile(string filePath, string tableName)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            try
            {
                string content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                var hands = SplitHands(content);
                if (hands.Count == 0) return;

                lock (_lock)
                {
                    if (!_states.TryGetValue(tableName, out var st))
                    { st = new TableState(); _states[tableName] = st; }

                    // FIX: При первом вызове просто запоминаем текущий last hand
                    // и выходим — не анализируем историю (иначе сразу алерт при старте).
                    if (st.LastHandId == null)
                    {
                        st.LastHandId = hands[hands.Count - 1].Id;
                        return;
                    }

                    // Находим индекс после LastHandId
                    int startIdx = -1;
                    for (int i = 0; i < hands.Count; i++)
                        if (hands[i].Id == st.LastHandId) { startIdx = i + 1; break; }

                    // LastHandId не найден (файл ротировался) — берём только последний хэнд
                    if (startIdx < 0) startIdx = Math.Max(0, hands.Count - 1);

                    for (int i = startIdx; i < hands.Count; i++)
                    {
                        var (hid, htxt) = hands[i];
                        if (string.IsNullOrEmpty(hid)) continue;
                        // Пропускаем хэнды без Seat-строк (некорректные/неполные)
                        if (!HandHasSeats(htxt)) continue;

                        if (HandHasFish(htxt))
                        {
                            st.NoFishStreak = 0;
                            st.AlertShown = false; // фиш вернулся — снимаем алерт
                        }
                        else
                        {
                            st.NoFishStreak++;
                        }
                    }

                    st.LastHandId = hands[hands.Count - 1].Id;

                    if (st.NoFishStreak >= HandsThreshold
                        && !st.AlertShown
                        && DateTime.Now > st.SnoozedUntil)
                    {
                        st.AlertShown = true;
                        OnNoFishAlert?.Invoke(tableName);
                    }
                }
            }
            catch { }
        }

        public void Snooze(string tableName)
        {
            lock (_lock)
            {
                if (!_states.TryGetValue(tableName, out var st)) return;
                st.AlertShown = false;
                st.NoFishStreak = 0;
                st.SnoozedUntil = DateTime.Now.AddMinutes(SnoozeMinutes);
            }
        }

        public void Reset(string tableName)
        {
            lock (_lock) { _states.Remove(tableName); }
        }

        public void ResetAll()
        {
            lock (_lock) { _states.Clear(); }
        }

        // ── Хелперы ──────────────────────────────────────────────────
        private bool HandHasFish(string handText)
        {
            var mSeats = Regex.Matches(handText,
                @"Seat #?(\d+):\s+(.+?)\s+\([\d,. $]+\s*(?:in\s+chips)?\)",
                RegexOptions.IgnoreCase);
            foreach (Match ms in mSeats)
            {
                string nick = ms.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(nick) && _h2nReader.IsFish(nick)) return true;
            }
            return false;
        }

        private static bool HandHasSeats(string handText) =>
            Regex.IsMatch(handText, @"Seat #?\d+:\s+\S+", RegexOptions.IgnoreCase);

        private static List<(string Id, string Text)> SplitHands(string content)
        {
            var result = new List<(string, string)>();
            foreach (var part in content.Split(new[] { "1WinPoker Hand" }, StringSplitOptions.None))
            {
                if (!part.Contains("*** SUMMARY ***")) continue;
                string full = "1WinPoker Hand" + part;
                var m = Regex.Match(full, @"1WinPoker Hand #?(\d+)");
                if (m.Success) result.Add((m.Groups[1].Value, full.Trim()));
            }
            return result;
        }
    }
}