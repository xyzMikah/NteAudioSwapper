using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Alias everything ambiguous so WPF types always win
using WpfColor        = System.Windows.Media.Color;
using WpfBrush        = System.Windows.Media.Brush;
using WpfPoint        = System.Windows.Point;
using WpfMessageBox   = System.Windows.MessageBox;
using WpfDataFormats  = System.Windows.DataFormats;
using WpfDragEffects  = System.Windows.DragDropEffects;

namespace NtePakTool
{
    public class SongIdEntry
    {
        public string WemName     { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ImageName   { get; set; } = "";
    }

    public class SongCard : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public string       WemName     { get; init; } = "";
        public string       DisplayName { get; init; } = "";
        public string?      ImageName   { get; init; }
        public BitmapImage? CoverImage  { get; init; }
        public BitmapImage? AddedIcon   { get; init; }

        private string? _assignedWav;
        public string? AssignedWav
        {
            get => _assignedWav;
            set
            {
                _assignedWav = value;
                Notify(nameof(AssignedWav));
                Notify(nameof(AssignedFileName));
                Notify(nameof(AssignedOverlayVisibility));
                Notify(nameof(BorderColor));
                Notify(nameof(TooltipText));
            }
        }

        public string AssignedFileName =>
            AssignedWav == null ? "" : Path.GetFileName(AssignedWav);

        public Visibility AssignedOverlayVisibility =>
            AssignedWav != null ? Visibility.Visible : Visibility.Collapsed;

        private bool _dragOver;
        public bool IsDragOver
        {
            get => _dragOver;
            set { _dragOver = value; Notify(nameof(DragOverVisibility)); }
        }
        public Visibility DragOverVisibility =>
            _dragOver ? Visibility.Visible : Visibility.Collapsed;

        public WpfBrush BorderColor => AssignedWav != null
            ? new SolidColorBrush(WpfColor.FromRgb(0x22, 0xC5, 0x5E))
            : new SolidColorBrush(WpfColor.FromRgb(0x32, 0x32, 0x5A));

        public string TooltipText => AssignedWav != null
            ? $"Assigned: {AssignedFileName}\nTarget: {WemName}\nClick or drop a new WAV to replace."
            : $"Click or drop a WAV here\n{WemName}";
    }

    public partial class MainWindow : Window
    {
        // ── Win32 for borderless resize ───────────────────────────────────
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();

        private const int WM_NCHITTEST  = 0x0084;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTLEFT        = 10;
        private const int HTRIGHT       = 11;
        private const int HTTOP         = 12;
        private const int HTTOPLEFT     = 13;
        private const int HTTOPRIGHT    = 14;
        private const int HTBOTTOM      = 15;
        private const int HTBOTTOMLEFT  = 16;
        private const int HTBOTTOMRIGHT = 17;
        private const int HTCLIENT      = 1;
        private const int BORDER        = 8; // px hit zone

        // ── Card snap constants ───────────────────────────────────────────
        private const double CardWidth   = 155 + 10; // card + margins
        private const double WindowChrome = 36 + 2;  // border + padding both sides
        private const int    MinColumns   = 1;
        private const int    MaxColumns   = 5;

        // ── Inertia scroll state ──────────────────────────────────────────
        private ScrollViewer? _cardScroller;
        private double        _scrollVelocity  = 0;
        private double        _scrollTarget    = 0;
        private System.Windows.Threading.DispatcherTimer? _scrollTimer;

        // ── Fields ────────────────────────────────────────────────────────
        private string wwiseConsolePath = "";
        private string wprojPath        = "";
        private string upakExePath      = "";
        private readonly string resourcesDir = Path.Combine(Path.GetTempPath(), "NTEModBuilder_Resources");
        private readonly ObservableCollection<SongCard> cards = new();
        private BitmapImage? _addedIcon;
        private bool _dialogOpen = false;

        public MainWindow()
        {
            InitializeComponent();
            SongGrid.ItemsSource = cards;
            ExtractEmbeddedResources();
            _addedIcon = LoadImageByName("AddedSong.png");
            LoadSongCards();
            wwiseConsolePath = DetectWwiseConsolePath();
            UpdateWwiseStatus();

            // Hook WndProc for borderless resize
            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            };

            // Snap window on resize
            SizeChanged += Window_SizeChanged;

            // Set initial snap width (4 columns)
            Loaded += (s, e) =>
            {
                SnapToColumns(4);
                SetupInertiaScroll();
            };
        }

        // ── WndProc: allow resize from all edges + corners ────────────────
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST)
            {
                int x = (int)(lParam.ToInt64() & 0xFFFF);
                int y = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                // Convert screen coords to window coords
                var pos    = new WpfPoint(x, y);
                var origin = PointToScreen(new WpfPoint(0, 0));
                double wx  = pos.X - origin.X;
                double wy  = pos.Y - origin.Y;
                double w   = ActualWidth;
                double h   = ActualHeight;

                bool left   = wx <= BORDER;
                bool right  = wx >= w - BORDER;
                bool top    = wy <= BORDER;
                bool bottom = wy >= h - BORDER;

                if (top    && left)  { handled = true; return (IntPtr)HTTOPLEFT; }
                if (top    && right) { handled = true; return (IntPtr)HTTOPRIGHT; }
                if (bottom && left)  { handled = true; return (IntPtr)HTBOTTOMLEFT; }
                if (bottom && right) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
                if (left)            { handled = true; return (IntPtr)HTLEFT; }
                if (right)           { handled = true; return (IntPtr)HTRIGHT; }
                if (top)             { handled = true; return (IntPtr)HTTOP; }
                if (bottom)          { handled = true; return (IntPtr)HTBOTTOM; }
            }
            return IntPtr.Zero;
        }

        // ── Snap to column count on resize ────────────────────────────────
        private bool _snapping = false;
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_snapping) return;
            // Determine closest column count
            double availWidth = e.NewSize.Width - WindowChrome;
            int cols = Math.Clamp((int)Math.Round(availWidth / CardWidth), MinColumns, MaxColumns);
            double snapWidth = cols * CardWidth + WindowChrome;

            if (Math.Abs(Width - snapWidth) > 1)
            {
                _snapping = true;
                // Animate to snapped width
                var anim = new DoubleAnimation(snapWidth, new Duration(TimeSpan.FromMilliseconds(120)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                anim.Completed += (s, _) => _snapping = false;
                BeginAnimation(WidthProperty, anim);
            }
        }

        private void SnapToColumns(int cols)
        {
            double snapWidth = cols * CardWidth + WindowChrome;
            _snapping = true;
            Width = snapWidth;
            _snapping = false;
        }

        // ── Inertia / acceleration scrolling ─────────────────────────────
        private void SetupInertiaScroll()
        {
            // Find the card ScrollViewer after layout
            _cardScroller = FindVisualChild<ScrollViewer>(SongGrid);
            if (_cardScroller == null) return;

            _scrollTarget = 0;

            _scrollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60fps
            _scrollTimer.Tick += ScrollTick;
            _scrollTimer.Start();

            // Intercept mouse wheel on the scroll viewer
            _cardScroller.PreviewMouseWheel += OnCardScrollWheel;
        }

        private void OnCardScrollWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            // Add velocity — negative delta = scroll down
            _scrollVelocity -= e.Delta * 0.8;
            _scrollTarget    = Math.Clamp(
                _cardScroller!.VerticalOffset + _scrollVelocity * 0.1,
                0, _cardScroller.ScrollableHeight);
        }

        private void ScrollTick(object? sender, EventArgs e)
        {
            if (_cardScroller == null) return;
            if (Math.Abs(_scrollVelocity) < 0.1) { _scrollVelocity = 0; return; }

            // Apply velocity to actual offset
            double current = _cardScroller.VerticalOffset;
            double next    = Math.Clamp(current + _scrollVelocity * 0.016, 0, _cardScroller.ScrollableHeight);
            _cardScroller.ScrollToVerticalOffset(next);

            // Decelerate — friction coefficient
            _scrollVelocity *= 0.88;

            if (next <= 0 || next >= _cardScroller.ScrollableHeight)
                _scrollVelocity = 0;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        // ── Load songs ────────────────────────────────────────────────────
        private void LoadSongCards()
        {
            List<SongIdEntry> entries = new();
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resName  = assembly.GetManifestResourceNames()
                    .FirstOrDefault(r => r.EndsWith("id.json", StringComparison.OrdinalIgnoreCase));

                string? raw = null;
                if (resName != null)
                {
                    using var stream = assembly.GetManifestResourceStream(resName)!;
                    using var reader = new StreamReader(stream);
                    raw = reader.ReadToEnd();
                    Log("Loaded id.json from embedded resources.");
                }
                else
                {
                    string[] candidates = {
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "id.json"),
                        Path.Combine(resourcesDir, "id.json"),
                    };
                    string? jsonPath = candidates.FirstOrDefault(File.Exists);
                    if (jsonPath != null) { raw = File.ReadAllText(jsonPath); Log($"Loaded id.json from: {jsonPath}"); }
                }

                if (raw != null)
                {
                    entries = JsonSerializer.Deserialize<List<SongIdEntry>>(raw,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                    Log($"Loaded {entries.Count} song(s).");
                }
                else Log("id.json not found — no cards loaded.");
            }
            catch (Exception ex) { Log($"WARNING: Could not read id.json — {ex.Message}"); }

            foreach (var e in entries)
                cards.Add(new SongCard
                {
                    WemName     = e.WemName,
                    DisplayName = e.DisplayName,
                    ImageName   = e.ImageName,
                    CoverImage  = LoadImageByName(e.ImageName),
                    AddedIcon   = _addedIcon,
                });
        }

        private BitmapImage? LoadImageByName(string imageName)
        {
            if (string.IsNullOrWhiteSpace(imageName)) return null;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resName  = assembly.GetManifestResourceNames()
                    .FirstOrDefault(r => r.EndsWith(imageName, StringComparison.OrdinalIgnoreCase));
                if (resName != null)
                {
                    using var stream = assembly.GetManifestResourceStream(resName)!;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource  = stream;
                    bmp.CacheOption   = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                string[] candidates = {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, imageName),
                    Path.Combine(resourcesDir, imageName),
                };
                string? path = candidates.FirstOrDefault(File.Exists);
                if (path != null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource   = new Uri(path, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { }
            return null;
        }

        // ── Card interactions ─────────────────────────────────────────────
        private void Card_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_dialogOpen) return;
            if (sender is not FrameworkElement fe || fe.Tag is not SongCard card) return;
            _dialogOpen = true;
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Filter      = "WAV Files|*.wav",
                    Multiselect = false,
                    Title       = $"Select WAV for: {card.DisplayName}"
                };
                if (ofd.ShowDialog() == true)
                {
                    card.AssignedWav = ofd.FileName;
                    Log($"Assigned: {Path.GetFileName(ofd.FileName)}  →  {card.WemName}  ({card.DisplayName})");
                }
            }
            finally { _dialogOpen = false; }
        }

        private void Card_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop)) { e.Effects = WpfDragEffects.None; return; }
            var files   = (string[])e.Data.GetData(WpfDataFormats.FileDrop);
            bool hasWav = files.Any(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            e.Effects   = hasWav ? WpfDragEffects.Copy : WpfDragEffects.None;
            if (sender is FrameworkElement fe && fe.Tag is SongCard card) card.IsDragOver = hasWav;
        }

        private void Card_DragLeave(object sender, System.Windows.DragEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.Tag is SongCard card) card.IsDragOver = false;
        }

        private void Card_Drop(object sender, System.Windows.DragEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.Tag is SongCard card) card.IsDragOver = false;
            if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(WpfDataFormats.FileDrop);
            string? wav = files.FirstOrDefault(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            if (wav == null) { Log("Drop ignored — not a WAV."); return; }
            if (sender is FrameworkElement fe2 && fe2.Tag is SongCard t)
            {
                t.AssignedWav = wav;
                Log($"Assigned: {Path.GetFileName(wav)}  →  {t.WemName}  ({t.DisplayName})");
            }
        }

        // ── Window drag (title bar only) ──────────────────────────────────
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        // ── Randomize ─────────────────────────────────────────────────────
        private static readonly Random _rng = new();

        // Album groups matching id.json ImageName
        private static readonly HashSet<string> NteImages     = new() { "ost.png" };
        private static readonly HashSet<string> PersonaImages = new() { "p5_256.png", "p5x_256.png" };
        private static readonly HashSet<string> HTImages      = new() { "ht_256.png" };

        private void RandomizeBtn_Click(object sender, RoutedEventArgs e)
        {
            RandomizePopup.IsOpen = true;
        }

        private void Rand_NTE_Click(object sender, RoutedEventArgs e)
        {
            RandomizePopup.IsOpen = false;
            PromptAndRandomize(c => NteImages.Contains(c.ImageName ?? "ost.png"), "NTE originals");
        }

        private void Rand_Persona_Click(object sender, RoutedEventArgs e)
        {
            RandomizePopup.IsOpen = false;
            PromptAndRandomize(c => PersonaImages.Contains(c.ImageName ?? ""), "Persona");
        }

        private void Rand_HT_Click(object sender, RoutedEventArgs e)
        {
            RandomizePopup.IsOpen = false;
            PromptAndRandomize(c => HTImages.Contains(c.ImageName ?? ""), "Tower of Fantasy");
        }

        private void Rand_All_Click(object sender, RoutedEventArgs e)
        {
            RandomizePopup.IsOpen = false;
            PromptAndRandomize(_ => true, "all songs");
        }

        private void PromptAndRandomize(Func<SongCard, bool> filter, string label)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title       = $"Pick WAV files for [{label}] slots",
                Filter      = "WAV Files|*.wav",
                Multiselect = true,
            };
            if (ofd.ShowDialog() != true) return;

            var wavFiles = ofd.FileNames.ToList();
            if (wavFiles.Count == 0) return;

            var targets = cards.Where(filter).ToList();
            if (targets.Count == 0) { Log($"No cards match filter: {label}"); return; }

            // Shuffle WAVs
            var shuffled = wavFiles.OrderBy(_ => _rng.Next()).ToList();

            // Too many WAVs — trim to targets count, toast the removed ones
            List<string> removed = new();
            if (shuffled.Count > targets.Count)
            {
                removed = shuffled.Skip(targets.Count)
                                  .Select(Path.GetFileName)
                                  .ToList()!;
                shuffled = shuffled.Take(targets.Count).ToList();
            }

            // Assign 1 WAV per card — no duplicates ever
            // If fewer WAVs than cards, only assign to as many cards as we have WAVs
            for (int i = 0; i < Math.Min(shuffled.Count, targets.Count); i++)
                targets[i].AssignedWav = shuffled[i];

            Log($"Randomized {shuffled.Count} [{label}] card(s).");

            // Show yellow toast if songs were removed
            if (removed.Count > 0)
            {
                string names = string.Join(", ", removed.Take(8));
                if (removed.Count > 8) names += $" +{removed.Count - 8} more";
                ShowToast($"Too many songs — {removed.Count} removed:\n{names}");
            }
        }

        private async void ShowToast(string message)
        {
            ToastText.Text      = message;
            ToastBorder.Visibility = Visibility.Visible;

            // Slide in from right
            var slideIn = new DoubleAnimation(400, 0, new Duration(TimeSpan.FromMilliseconds(300)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ToastTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);

            // Wait 4 seconds
            await Task.Delay(4000);

            // Slide out to the right
            var slideOut = new DoubleAnimation(0, 400, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            slideOut.Completed += (s, e) => ToastBorder.Visibility = Visibility.Collapsed;
            ToastTranslate.BeginAnimation(TranslateTransform.XProperty, slideOut);
        }

        // ── Clear / Build ─────────────────────────────────────────────────
        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in cards) c.AssignedWav = null;
            Log("All assignments cleared.");
        }

        private async void BuildMod_Click(object sender, RoutedEventArgs e)
        {
            var assigned = cards.Where(c => c.AssignedWav != null).ToList();
            if (assigned.Count == 0)
            {
                WpfMessageBox.Show("No songs assigned!\n\nClick or drag a WAV onto at least one song card.",
                    "NTE Audio Swapper", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!File.Exists(wwiseConsolePath))
            {
                try { Process.Start(new ProcessStartInfo("https://www.audiokinetic.com/en/download-launcher/?platform=1") { UseShellExecute = true }); } catch { }
                WpfMessageBox.Show("WwiseConsole.exe not found.\n\nThe download page has been opened.\nPlease install Wwise version 2025.1.7.9143.",
                    "Wwise Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnBuild.IsEnabled  = false;
            string modName      = string.IsNullOrWhiteSpace(TxtModName.Text) ? "AudioMod_P" : TxtModName.Text.Trim();
            string desktopPath  = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string outputFolder = Path.Combine(desktopPath, modName);
            string outputPak    = Path.Combine(outputFolder, modName + ".pak");
            string tempDir      = Path.Combine(Path.GetTempPath(), "NTEAudioModBuilder_" + DateTime.Now.Ticks);
            string audioWorkDir = Path.Combine(tempDir, "AudioWork");
            string wemOutDir    = Path.Combine(tempDir, "WemOut");

            try
            {
                Directory.CreateDirectory(audioWorkDir);
                Directory.CreateDirectory(wemOutDir);
                Directory.CreateDirectory(outputFolder);
                Log($"Starting build: {assigned.Count} song(s)...");

                Log("Step 1: Converting WAV → WEM via Wwise...");
                var xml = new StringBuilder();
                xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                xml.AppendLine($"<ExternalSourcesList SchemaVersion=\"1\" Root=\"{audioWorkDir}\">");
                foreach (var c in assigned)
                {
                    string wemNoExt = Path.GetFileNameWithoutExtension(c.WemName);
                    string destWav  = Path.Combine(audioWorkDir, wemNoExt + ".wav");
                    File.Copy(c.AssignedWav!, destWav, true);
                    xml.AppendLine($"  <Source Path=\"{Path.GetFileName(destWav)}\" Conversion=\"Vorbis Quality High\"/>");
                }
                xml.AppendLine("</ExternalSourcesList>");
                string wsourcesPath = Path.Combine(tempDir, "list.wsources");
                File.WriteAllText(wsourcesPath, xml.ToString(), Encoding.UTF8);
                await RunProcessAsync(wwiseConsolePath,
                    $"convert-external-source \"{wprojPath}\" --source-file \"{wsourcesPath}\" --output \"{wemOutDir}\"");

                string windowsOut = Path.Combine(wemOutDir, "Windows");
                if (Directory.Exists(windowsOut))
                    foreach (var f in Directory.GetFiles(windowsOut))
                        File.Move(f, Path.Combine(wemOutDir, Path.GetFileName(f)), true);

                foreach (var c in assigned)
                    if (!File.Exists(Path.Combine(wemOutDir, c.WemName)))
                        throw new Exception($"WEM conversion failed for: {c.WemName}");
                Log("WEM conversion complete.");

                Log("Step 2: Staging pak folder structure...");
                string pakResponsePath = Path.Combine(tempDir, "pak_response.txt");
                using (var sw = new StreamWriter(pakResponsePath, false, new UTF8Encoding(false)))
                    foreach (var c in assigned)
                        sw.WriteLine($"\"{Path.Combine(wemOutDir, c.WemName)}\" \"../../../HT/Content/WwiseAudio/Media/{c.WemName}\"");

                Log("Step 3: Packing into .pak...");
                if (!File.Exists(upakExePath)) throw new Exception("UnrealPak.exe not found.");
                await RunProcessAsync(upakExePath, $"\"{outputPak}\" -create=\"{pakResponsePath}\"");
                if (!File.Exists(outputPak)) throw new Exception("UnrealPak finished but pak was not created.");

                Log("Copying dummy files...");
                var assembly = Assembly.GetExecutingAssembly();
                foreach (var ext in new[] { "sig", "ucas", "utoc" })
                {
                    using var stream = assembly.GetManifestResourceStream($"NtePakTool.Resources.dummy.{ext}");
                    if (stream != null)
                    {
                        using var file = File.Create(Path.Combine(outputFolder, modName + "." + ext));
                        stream.CopyTo(file);
                        Log($"Copied dummy.{ext} → {modName}.{ext}");
                    }
                    else Log($"WARNING: dummy.{ext} not found!");
                }

                Log($"Done! Saved to: {outputFolder}");
                WpfMessageBox.Show($"Mod built successfully!\n\nFolder saved to Desktop:\n{modName}",
                    "Build Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                WpfMessageBox.Show($"Build failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnBuild.IsEnabled = true;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ── Resource extraction ───────────────────────────────────────────
        private void ExtractEmbeddedResources()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                foreach (var resource in assembly.GetManifestResourceNames())
                {
                    if (!resource.StartsWith("NtePakTool.Resources.")) continue;
                    string relative = resource.Substring("NtePakTool.Resources.".Length)
                        .Replace("WwiseProject.Conversion_Settings.", "WwiseProject\\Conversion Settings\\")
                        .Replace("WwiseProject.", "WwiseProject\\");
                    int lastDot = relative.LastIndexOf('.');
                    string outPath = lastDot >= 0
                        ? Path.Combine(resourcesDir, relative[..lastDot].Replace('.', '\\') + relative[lastDot..])
                        : Path.Combine(resourcesDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    using var stream = assembly.GetManifestResourceStream(resource)!;
                    using var file   = File.Create(outPath);
                    stream.CopyTo(file);
                }
                wprojPath   = Path.Combine(resourcesDir, "WwiseProject", "NTE.wproj");
                upakExePath = Path.Combine(resourcesDir, "UnrealPak.exe");
                Log($"Resources ready. wproj={File.Exists(wprojPath)} upak={File.Exists(upakExePath)}");
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Failed to extract resources:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Wwise detection ───────────────────────────────────────────────
        private string DetectWwiseConsolePath()
        {
            const string subPath = @"Authoring\x64\Release\bin\WwiseConsole.exe";
            string? root = Environment.GetEnvironmentVariable("WWISEROOT");
            if (!string.IsNullOrEmpty(root))
            {
                string p = Path.Combine(root.Trim('"'), subPath);
                if (File.Exists(p)) return p;
            }
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                foreach (var baseDir in new[] { "Audiokinetic", @"Program Files\Audiokinetic", @"Program Files (x86)\Audiokinetic" })
                {
                    string searchDir = Path.Combine(drive.Name, baseDir);
                    if (!Directory.Exists(searchDir)) continue;
                    try
                    {
                        foreach (var dir in Directory.GetDirectories(searchDir, "*Wwise*", SearchOption.TopDirectoryOnly))
                        {
                            string p = Path.Combine(dir, subPath);
                            if (File.Exists(p)) return p;
                        }
                    }
                    catch { }
                }
            return "";
        }

        private void UpdateWwiseStatus()
        {
            if (File.Exists(wwiseConsolePath))
            {
                TxtWwiseStatus.Text       = "wwise ready";
                TxtWwiseStatus.Foreground = new SolidColorBrush(WpfColor.FromRgb(0x46, 0xF0, 0xD2));
            }
            else
            {
                TxtWwiseStatus.Text       = "wwise not found — install 2025.1.7.9143";
                TxtWwiseStatus.Foreground = new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x44, 0x66));
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private Task RunProcessAsync(string exe, string args)
        {
            var tcs = new TaskCompletionSource<bool>();
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                },
                EnableRaisingEvents = true
            };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) Log("> " + e.Data); };
            p.ErrorDataReceived  += (s, e) => { if (e.Data != null) Log("! " + e.Data); };
            p.Exited += (s, e) => { tcs.SetResult(true); p.Dispose(); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return tcs.Task;
        }

        private void Log(string msg) =>
            Dispatcher.Invoke(() =>
            {
                TxtLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
                LogScroller.ScrollToEnd();
            });

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _scrollTimer?.Stop();
            try { Directory.Delete(resourcesDir, true); } catch { }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
