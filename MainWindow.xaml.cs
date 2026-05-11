using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GameShell_WinUI_V9
{
    public class GameItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string CoverPath { get; set; } = string.Empty;
        public int TotalSeconds { get; set; }
        public string LastPlayed { get; set; } = string.Empty;
        public bool RunAsAdmin { get; set; }
        public bool IsBat { get; set; }
        public bool ManualTimer { get; set; }
    }

    public class SessionItem
    {
        public string Date { get; set; } = string.Empty;
        public string Duration { get; set; } = string.Empty;
    }

    public sealed partial class MainWindow : Window
    {
        private const string DB = "gameshell.db";
        private ObservableCollection<GameItem> _allGames = new();
        private ObservableCollection<GameItem> _filteredGames = new();
        private ObservableCollection<SessionItem> _sessions = new();
        private GameItem? _selectedGame;
        private Process? _activeProcess;
        private DateTime _sessionStart;
        private DispatcherTimer _sysTimer = null!;
        private bool _manualTimerRunning = false;
        private bool _sessionActive = false;

        // Estilos para BtnStopTimer — se cachean tras el primer uso
        private Style? _redButtonStyle;
        private Style? _defaultButtonStyle;

        public MainWindow()
        {
            InitializeComponent();
            InitDb();
            LoadGames();
            GameListView.ItemsSource = _filteredGames;
            SessionListView.ItemsSource = _sessions;
            StartSysMonitor();

            // Cachear estilos una vez que el árbol visual está listo
            _defaultButtonStyle = BtnStopTimer.Style;
        }

        // Aplica el estilo rojo o gris al botón Detener y gestiona el tooltip
        void SetStopButtonActive(bool active, bool isBat)
        {
            if (active)
            {
                _redButtonStyle ??= (Style)((Grid)Content).Resources["RedButtonStyle"];
                BtnStopTimer.Style = _redButtonStyle;
                BtnStopTimer.IsEnabled = true;
                ToolTipService.SetToolTip(BtnStopTimer, null);
            }
            else
            {
                BtnStopTimer.Style = _defaultButtonStyle;
                BtnStopTimer.IsEnabled = false;
                if (!isBat)
                {
                    ToolTipService.SetToolTip(BtnStopTimer, new ToolTip
                    {
                        Content = "Solo archivos .bat",
                        Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom
                    });
                }
                else
                {
                    ToolTipService.SetToolTip(BtnStopTimer, null);
                }
            }
        }

        void InitDb()
        {
            using var con = new SqliteConnection($"Data Source={DB}");
            con.Open();
            new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS games (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT, path TEXT, cover TEXT,
                    total_seconds INTEGER DEFAULT 0,
                    last_played TEXT, run_as_admin INTEGER DEFAULT 0,
                    manual_timer INTEGER DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id INTEGER, started TEXT, duration_seconds INTEGER
                );", con).ExecuteNonQuery();
            try { new SqliteCommand("ALTER TABLE games ADD COLUMN manual_timer INTEGER DEFAULT 0", con).ExecuteNonQuery(); }
            catch { }
        }

        void LoadGames()
        {
            _allGames.Clear();
            using var con = new SqliteConnection($"Data Source={DB}");
            con.Open();
            using var r = new SqliteCommand("SELECT id,name,path,cover,total_seconds,last_played,run_as_admin,manual_timer FROM games ORDER BY name", con).ExecuteReader();
            while (r.Read())
                _allGames.Add(new GameItem
                {
                    Id = r.GetInt32(0),
                    Name = r.GetString(1),
                    Path = r.GetString(2),
                    CoverPath = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                    TotalSeconds = r.GetInt32(4),
                    LastPlayed = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                    RunAsAdmin = r.GetInt32(6) == 1,
                    IsBat = r.GetString(2).EndsWith(".bat", StringComparison.OrdinalIgnoreCase),
                    ManualTimer = r.GetInt32(7) == 1
                });
            FilterGames(SearchBox.Text);
        }

        void FilterGames(string query)
        {
            _filteredGames.Clear();
            foreach (var g in _allGames)
                if (string.IsNullOrWhiteSpace(query) || g.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    _filteredGames.Add(g);
        }

        void SaveSession(int gid, DateTime start, int dur)
        {
            using var con = new SqliteConnection($"Data Source={DB}");
            con.Open();
            new SqliteCommand($"INSERT INTO sessions (game_id,started,duration_seconds) VALUES ({gid},'{start:o}',{dur})", con).ExecuteNonQuery();
            new SqliteCommand($"UPDATE games SET total_seconds=total_seconds+{dur}, last_played='{start:o}' WHERE id={gid}", con).ExecuteNonQuery();
        }

        List<SessionItem> GetSessions(int gid)
        {
            var list = new List<SessionItem>();
            using var con = new SqliteConnection($"Data Source={DB}");
            con.Open();
            using var r = new SqliteCommand($"SELECT started,duration_seconds FROM sessions WHERE game_id={gid} ORDER BY started DESC LIMIT 10", con).ExecuteReader();
            while (r.Read())
            {
                var dt = DateTime.Parse(r.GetString(0));
                list.Add(new SessionItem { Date = dt.ToString("dd/MM/yyyy HH:mm"), Duration = FmtTime(r.GetInt32(1)) });
            }
            return list;
        }

        int[] GetWeekActivity(int gid)
        {
            var mins = new int[7];
            var today = DateTime.Today;
            int dayOfWeek = (int)today.DayOfWeek;
            int offset = dayOfWeek == 0 ? 6 : dayOfWeek - 1;
            var weekStart = today.AddDays(-offset);

            using var con = new SqliteConnection($"Data Source={DB}");
            con.Open();
            using var r = new SqliteCommand($"SELECT started, duration_seconds FROM sessions WHERE game_id={gid}", con).ExecuteReader();
            while (r.Read())
            {
                var dt = DateTime.Parse(r.GetString(0)).Date;
                int diff = (dt - weekStart).Days;
                if (diff >= 0 && diff < 7)
                    mins[diff] += r.GetInt32(1) / 60;
            }
            return mins;
        }

        static string FmtTime(int s) => s >= 3600 ? $"{s / 3600}h {(s % 3600) / 60}m" : $"{s / 60}m";
        static string FmtMins(int m) => m >= 60 ? $"{m / 60}h {m % 60}m" : $"{m}m";

        void SelectGame(GameItem g)
        {
            _selectedGame = g;
            LblGameName.Text = g.Name;
            LblGameTime.Text = $"Tiempo total: {FmtTime(g.TotalSeconds)}";
            LblLastPlayed.Text = !string.IsNullOrEmpty(g.LastPlayed)
                ? $"Última sesión: {DateTime.Parse(g.LastPlayed):dd/MM/yyyy HH:mm}"
                : "Sin sesiones aún";
            BtnLaunch.IsEnabled = _activeProcess == null && !_manualTimerRunning;

            // Rojo y clickeable solo si es .bat con cronómetro activo, gris en cualquier otro caso
            bool stopActive = g.IsBat && _manualTimerRunning && _sessionActive;
            SetStopButtonActive(stopActive, g.IsBat);

            if (!string.IsNullOrEmpty(g.CoverPath) && File.Exists(g.CoverPath))
                GameCover.Source = new BitmapImage(new Uri(g.CoverPath));
            else
                GameCover.Source = null;

            _sessions.Clear();
            foreach (var s in GetSessions(g.Id)) _sessions.Add(s);
            RefreshStats();
            RefreshActivity(g.Id);
        }

        void RefreshStats()
        {
            int total = _allGames.Sum(g => g.TotalSeconds);
            var most = _allGames.OrderByDescending(g => g.TotalSeconds).FirstOrDefault();
            LblStatTotal.Text = FmtTime(total);
            LblStatMost.Text = most != null ? $"{most.Name} ({FmtTime(most.TotalSeconds)})" : "—";

            using var con = new SqliteConnection($"Data Source={DB}");
            con.Open();
            using var r = new SqliteCommand("SELECT DISTINCT substr(started,1,10) FROM sessions", con).ExecuteReader();
            int days = 0;
            while (r.Read()) days++;
            LblStatDays.Text = days.ToString();
        }

        void RefreshActivity(int gid)
        {
            var mins = GetWeekActivity(gid);
            int maxMins = mins.Max() == 0 ? 1 : mins.Max();
            double maxH = BarContainer.ActualHeight > 0 ? BarContainer.ActualHeight : 200;

            var bars = new[] { BarLun, BarMar, BarMie, BarJue, BarVie, BarSab, BarDom };
            for (int i = 0; i < 7; i++)
                bars[i].Height = maxH * mins[i] / maxMins;

            LblYMax.Text = FmtMins(maxMins);
            LblYMid.Text = FmtMins(maxMins / 2);
        }

        void GameListView_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (GameListView.SelectedItem is GameItem g) SelectGame(g);
        }

        void SearchBox_TextChanged(AutoSuggestBox s, AutoSuggestBoxTextChangedEventArgs e)
            => FilterGames(s.Text);

        async void BtnAddGame_Click(object s, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".bat");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var tb = new TextBox { PlaceholderText = "Nombre del juego", Width = 280 };
            var dlg = new ContentDialog
            {
                Title = "Agregar juego",
                Content = tb,
                PrimaryButtonText = "Agregar",
                CloseButtonText = "Cancelar",
                XamlRoot = Content.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            var name = tb.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;

            using var con = new SqliteConnection($"Data Source={DB}");
            con.Open();
            new SqliteCommand($"INSERT INTO games (name,path) VALUES ('{name.Replace("'", "''")}','{file.Path.Replace("'", "''")}' )", con).ExecuteNonQuery();
            LoadGames();
        }

        async void BtnDeleteGame_Click(object s, RoutedEventArgs e)
        {
            if (_selectedGame == null) return;
            var dlg = new ContentDialog
            {
                Title = "Eliminar juego",
                Content = $"¿Eliminar '{_selectedGame.Name}' y todas sus sesiones?",
                PrimaryButtonText = "Eliminar",
                CloseButtonText = "Cancelar",
                XamlRoot = Content.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            using var con = new SqliteConnection($"Data Source={DB}");
            con.Open();
            new SqliteCommand($"DELETE FROM sessions WHERE game_id={_selectedGame.Id}", con).ExecuteNonQuery();
            new SqliteCommand($"DELETE FROM games WHERE id={_selectedGame.Id}", con).ExecuteNonQuery();
            _selectedGame = null;
            LblGameName.Text = "—";
            LblGameTime.Text = "Tiempo total: —";
            LblLastPlayed.Text = string.Empty;
            BtnLaunch.IsEnabled = false;
            SetStopButtonActive(false, false);
            GameCover.Source = null;
            _sessions.Clear();
            LoadGames();
        }

        async void BtnGameProps_Click(object s, RoutedEventArgs e)
        {
            if (_selectedGame == null) return;

            var chkAdmin = new CheckBox { Content = "Lanzar como administrador", IsChecked = _selectedGame.RunAsAdmin };
            var btnCover = new Button { Content = "Cambiar portada...", Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0) };
            string? newCoverPath = null;
            btnCover.Click += async (_, _) =>
            {
                var p = new FileOpenPicker();
                InitializeWithWindow.Initialize(p, WindowNative.GetWindowHandle(this));
                p.FileTypeFilter.Add(".png"); p.FileTypeFilter.Add(".jpg"); p.FileTypeFilter.Add(".jpeg");
                var f = await p.PickSingleFileAsync();
                if (f != null) { newCoverPath = f.Path; btnCover.Content = System.IO.Path.GetFileName(f.Path); }
            };

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(chkAdmin);
            panel.Children.Add(btnCover);

            var dlg = new ContentDialog
            {
                Title = $"Propiedades — {_selectedGame.Name}",
                Content = panel,
                PrimaryButtonText = "Guardar",
                CloseButtonText = "Cancelar",
                XamlRoot = Content.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            using var con = new SqliteConnection($"Data Source={DB}");
            con.Open();
            int admin = chkAdmin.IsChecked == true ? 1 : 0;
            string cover = newCoverPath != null
                ? $"'{newCoverPath.Replace("'", "''")}'"
                : (string.IsNullOrEmpty(_selectedGame.CoverPath) ? "NULL" : $"'{_selectedGame.CoverPath.Replace("'", "''")}'");
            new SqliteCommand($"UPDATE games SET run_as_admin={admin}, cover={cover} WHERE id={_selectedGame.Id}", con).ExecuteNonQuery();
            LoadGames();
            var updated = _allGames.FirstOrDefault(g => g.Id == _selectedGame.Id);
            if (updated != null) SelectGame(updated);
        }

        void BtnLaunch_Click(object s, RoutedEventArgs e)
        {
            if (_selectedGame == null || _activeProcess != null || _manualTimerRunning) return;
            try
            {
                var psi = new ProcessStartInfo(_selectedGame.Path)
                {
                    WorkingDirectory = System.IO.Path.GetDirectoryName(_selectedGame.Path),
                    UseShellExecute = true,
                    Verb = _selectedGame.RunAsAdmin ? "runas" : ""
                };

                if (_selectedGame.IsBat)
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = $"/c \"{_selectedGame.Path}\"";
                }

                var proc = Process.Start(psi)!;
                _sessionStart = DateTime.Now;
                _sessionActive = true;
                BtnLaunch.IsEnabled = false;
                LblStatus.Text = $"▶ {_selectedGame.Name} en ejecución";

                if (_selectedGame.IsBat)
                {
                    // Fire and forget: ignorar cuando el .bat se cierre
                    // El cronómetro sigue hasta que el usuario presione Detener
                    _activeProcess = null;
                    _manualTimerRunning = true;
                    SetStopButtonActive(true, true);
                }
                else
                {
                    // .exe: monitorear el proceso, se detiene automáticamente al cerrar
                    _activeProcess = proc;
                    Task.Run(() => WatchProcess(_selectedGame));
                }
            }
            catch (Exception ex)
            {
                LblStatus.Text = $"Error: {ex.Message}";
                _sessionActive = false;
            }
        }

        void BtnStopTimer_Click(object s, RoutedEventArgs e)
        {
            // Guardia extra: solo actúa si hay cronómetro manual corriendo
            if (!_manualTimerRunning || !_sessionActive || _selectedGame == null) return;

            int dur = (int)(DateTime.Now - _sessionStart).TotalSeconds;
            SaveSession(_selectedGame.Id, _sessionStart, dur);

            _manualTimerRunning = false;
            _sessionActive = false;
            SetStopButtonActive(false, _selectedGame.IsBat);
            BtnLaunch.IsEnabled = true;
            LblStatus.Text = "Sesión guardada.";
            LoadGames();
            var updated = _allGames.FirstOrDefault(x => x.Id == _selectedGame.Id);
            if (updated != null) SelectGame(updated);
        }

        void WatchProcess(GameItem g)
        {
            _activeProcess?.WaitForExit();
            int dur = (int)(DateTime.Now - _sessionStart).TotalSeconds;
            SaveSession(g.Id, _sessionStart, dur);
            _activeProcess = null;
            _sessionActive = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                BtnLaunch.IsEnabled = true;
                LblStatus.Text = "Sesión guardada.";
                LoadGames();
                var updated = _allGames.FirstOrDefault(x => x.Id == g.Id);
                if (updated != null) SelectGame(updated);
            });
        }

        void StartSysMonitor()
        {
            _sysTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _sysTimer.Tick += async (_, _) => await UpdateSysStats();
            _sysTimer.Start();
        }

        async Task UpdateSysStats()
        {
            await Task.Run(() =>
            {
                float cpu = 0, ram = 0, disk = 0;
                try
                {
                    using var q = new System.Management.ManagementObjectSearcher("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
                    foreach (System.Management.ManagementObject o in q.Get())
                        cpu = float.Parse(o["PercentProcessorTime"].ToString()!);
                }
                catch { }
                try
                {
                    using var q = new System.Management.ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
                    foreach (System.Management.ManagementObject o in q.Get())
                    {
                        long total = long.Parse(o["TotalVisibleMemorySize"].ToString()!);
                        long free = long.Parse(o["FreePhysicalMemory"].ToString()!);
                        ram = (float)(total - free) / total * 100;
                    }
                }
                catch { }
                try
                {
                    var di = new DriveInfo("C");
                    disk = (float)(1.0 - (double)di.AvailableFreeSpace / di.TotalSize) * 100;
                }
                catch { }

                DispatcherQueue.TryEnqueue(() =>
                {
                    LblCpu.Text = $"{cpu:0}%";
                    LblRam.Text = $"{ram:0}%";
                    LblDisk.Text = $"{disk:0}%";
                });
            });
        }
    }
}