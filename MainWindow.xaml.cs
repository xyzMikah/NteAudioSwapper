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
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;

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
                Notify(nameof(EmptyHintVisibility));
                Notify(nameof(BorderColor));
                Notify(nameof(TooltipText));
            }
        }

        public string AssignedFileName =>
            AssignedWav == null ? "" : Path.GetFileName(AssignedWav);

        public Visibility AssignedOverlayVisibility =>
            AssignedWav != null ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EmptyHintVisibility =>
            AssignedWav == null ? Visibility.Visible : Visibility.Collapsed;

        // ── drag state ──
        private bool _dragOver;
        public bool IsDragOver
        {
            get => _dragOver;
            set { _dragOver = value; Notify(nameof(DragOverVisibility)); }
        }
        public Visibility DragOverVisibility =>
            _dragOver ? Visibility.Visible : Visibility.Collapsed;

        // ── playback state ──
        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                _isPlaying = value;
                Notify(nameof(IsPlaying));
                Notify(nameof(PlayGlyph));
                Notify(nameof(PlayGlyphMargin));
                Notify(nameof(PlayBubbleBrush));
                Notify(nameof(PlayMenuText));
                Notify(nameof(BorderColor));
            }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = Math.Clamp(value, 0, 1); Notify(nameof(ProgressWidth)); }
        }
        public double ProgressWidth => _progress * 151;

        // Segoe MDL2: E768 = play, E769 = pause
        public string    PlayGlyph       => _isPlaying ? "\uE769" : "\uE768";
        public Thickness PlayGlyphMargin => _isPlaying ? new Thickness(0) : new Thickness(3, 0, 0, 0);
        public string    PlayMenuText    => _isPlaying ? "Pause" : "Play";
        public WpfBrush  PlayBubbleBrush => _isPlaying ? BubblePlaying : BubbleIdle;

        private static readonly WpfBrush BubbleIdle    = Frozen(WpfColor.FromArgb(0xB0, 0x00, 0x00, 0x00));
        private static readonly WpfBrush BubblePlaying = Frozen(WpfColor.FromRgb(0x22, 0xC5, 0x5E));
        private static readonly WpfBrush BorderPlaying = Frozen(WpfColor.FromRgb(0x60, 0xA5, 0xFA));
        private static readonly WpfBrush BorderAssigned= Frozen(WpfColor.FromRgb(0x22, 0xC5, 0x5E));
        private static readonly WpfBrush BorderIdle    = Frozen(WpfColor.FromRgb(0x34, 0x34, 0x52));
        private static WpfBrush Frozen(WpfColor c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public WpfBrush BorderColor =>
            _isPlaying ? BorderPlaying : AssignedWav != null ? BorderAssigned : BorderIdle;

        public string TooltipText => AssignedWav != null
            ? $"{AssignedFileName}\nClick to play / pause. Right-click for more."
            : "Click or drop an audio file here";
    }

    public partial class MainWindow : Window
    {
        // ── Win32 for borderless resize ───────────────────────────────────
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const int  WM_SIZING = 0x0214, WM_EXITSIZEMOVE = 0x0232;
        private bool _resizedThisMove, _sizedFromLeft;
        private EventHandler? _boundsAnim;

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
        private const int BORDER        = 8;  // px hit zone for edges
        private const int CORNER        = 26; // px hit zone for corners (covers the rounded part)

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
        private string upakExePath      = "";
        private string wav2wemPath      = "";
        private const  int    VorbisQuality = 6; // ~Wwise "Vorbis Quality High". Try 4 if a track glitches in-game.
        private readonly string resourcesDir = Path.Combine(Path.GetTempPath(), "NTEModBuilder_Resources");
        private readonly ObservableCollection<SongCard> cards = new();
        private BitmapImage? _addedIcon;
        private bool _dialogOpen = false;

        // ── Preview player ────────────────────────────────────────────────
        private WaveOutEvent?    _player;
        private AudioFileReader? _playerReader;
        private SongCard?        _nowPlaying;
        private DispatcherTimer? _progressTimer;

        // ── Filtering ─────────────────────────────────────────────────────
        private ICollectionView? _view;
        private string _filterMode = "all";
        private string _search     = "";

        public MainWindow()
        {
            InitializeComponent();
            SongGrid.ItemsSource = cards;
            ExtractEmbeddedResources();
            _addedIcon = LoadImageByName("AddedSong.png");
            LoadSongCards();

            _view = CollectionViewSource.GetDefaultView(cards);
            _view.Filter = o => o is SongCard c && FilterCard(c);
            foreach (var c in cards)
                c.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName != nameof(SongCard.AssignedWav)) return;
                    UpdateCounts();
                    if (_filterMode == "assigned") _view?.Refresh();
                };
            UpdateCounts();

            // Hook WndProc for borderless resize
            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            };

            // Restore last window size/position from %AppData%
            bool restored = LoadWindowSettings();

            // Open animation: fade + tiny zoom
            RootGrid.Opacity = 0;
            Loaded += (s, e) =>
            {
                if (restored) SnapToColumns(ColumnsFor(Width));
                else          SnapToColumns(4);
                SetupInertiaScroll();

                var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
                var dur  = new Duration(TimeSpan.FromMilliseconds(320));
                RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
                RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
                RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
            };
        }

        // ── WndProc: allow resize from all edges + corners ────────────────
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SIZING)
            {
                // wParam = which edge is dragged (1 left, 4 top-left, 7 bottom-left)
                int edge = wParam.ToInt32();
                _sizedFromLeft   = edge == 1 || edge == 4 || edge == 7;
                _resizedThisMove = true;
                StopBoundsAnimation();
            }
            else if (msg == WM_EXITSIZEMOVE)
            {
                // User let go of the edge → glide to the nearest column width
                if (_resizedThisMove) Dispatcher.BeginInvoke(new Action(SnapAnimated));
                _resizedThisMove = false;
            }
            else if (msg == WM_NCHITTEST)
            {
                // Signed 16-bit coords (negative on monitors left of / above the main one)
                long lp = lParam.ToInt64();
                int x = unchecked((short)(lp & 0xFFFF));
                int y = unchecked((short)((lp >> 16) & 0xFFFF));
                // PointFromScreen handles DPI scaling (screen px → WPF units)
                var p     = PointFromScreen(new WpfPoint(x, y));
                double wx = p.X, wy = p.Y;
                double w  = ActualWidth;
                double h  = ActualHeight;

                bool left   = wx <= BORDER;
                bool right  = wx >= w - BORDER;
                bool top    = wy <= BORDER;
                bool bottom = wy >= h - BORDER;

                // Rounded corners are see-through, so make each corner a bigger
                // zone: anywhere within CORNER px of a corner resizes both edges.
                bool nearL = wx <= CORNER, nearR = wx >= w - CORNER;
                bool nearT = wy <= CORNER, nearB = wy >= h - CORNER;

                if ((top    && nearL) || (left  && nearT)) { handled = true; return (IntPtr)HTTOPLEFT; }
                if ((top    && nearR) || (right && nearT)) { handled = true; return (IntPtr)HTTOPRIGHT; }
                if ((bottom && nearL) || (left  && nearB)) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
                if ((bottom && nearR) || (right && nearB)) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
                if (left)            { handled = true; return (IntPtr)HTLEFT; }
                if (right)           { handled = true; return (IntPtr)HTRIGHT; }
                if (top)             { handled = true; return (IntPtr)HTTOP; }
                if (bottom)          { handled = true; return (IntPtr)HTBOTTOM; }
            }
            return IntPtr.Zero;
        }

        // ── Snap to column count (after the user lets go) ───────────────────
        private static int ColumnsFor(double width) =>
            Math.Clamp((int)Math.Round((width - WindowChrome) / CardWidth), MinColumns, MaxColumns);

        private void SnapToColumns(int cols) => Width = cols * CardWidth + WindowChrome;

        private void SnapAnimated()
        {
            double target = ColumnsFor(ActualWidth) * CardWidth + WindowChrome;
            if (Math.Abs(ActualWidth - target) < 1) return;
            double left = _sizedFromLeft ? Left + (ActualWidth - target) : Left; // keep the edge you didn't drag in place
            AnimateBounds(left, Top, target, ActualHeight, 280);
        }

        // Moves + resizes in ONE call per frame (no jitter), eased out
        private void AnimateBounds(double toL, double toT, double toW, double toH, int ms)
        {
            StopBoundsAnimation();
            double fL = Left, fT = Top, fW = ActualWidth, fH = ActualHeight;
            var clock = Stopwatch.StartNew();
            _boundsAnim = (s, e) =>
            {
                double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
                double k = 1 - Math.Pow(1 - t, 4); // quartic ease-out
                SetBounds(fL + (toL - fL) * k, fT + (toT - fT) * k, fW + (toW - fW) * k, fH + (toH - fH) * k);
                if (t >= 1) StopBoundsAnimation();
            };
            CompositionTarget.Rendering += _boundsAnim;
        }

        private void StopBoundsAnimation()
        {
            if (_boundsAnim == null) return;
            CompositionTarget.Rendering -= _boundsAnim;
            _boundsAnim = null;
        }

        private void SetBounds(double l, double t, double w, double h)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var src  = PresentationSource.FromVisual(this);
            if (hwnd == IntPtr.Zero || src?.CompositionTarget == null)
            {
                Left = l; Top = t; Width = w; Height = h;
                return;
            }
            var m = src.CompositionTarget.TransformToDevice; // DIPs → real pixels
            SetWindowPos(hwnd, IntPtr.Zero,
                (int)Math.Round(l * m.M11), (int)Math.Round(t * m.M22),
                (int)Math.Round(w * m.M11), (int)Math.Round(h * m.M22),
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        // ── Window size saved in %AppData%\NTE Audio Swapper\window.json ──
        private class WindowSettings
        {
            public double Left   { get; set; }
            public double Top    { get; set; }
            public double Width  { get; set; }
            public double Height { get; set; }
        }

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NTE Audio Swapper", "window.json");

        private bool LoadWindowSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return false;
                var ws = JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(SettingsPath));
                if (ws == null || ws.Width < MinWidth || ws.Height < MinHeight) return false;

                // Only restore the position if the window would still be on a screen
                bool onScreen =
                    ws.Left + 100 > SystemParameters.VirtualScreenLeft &&
                    ws.Top  + 40  > SystemParameters.VirtualScreenTop  &&
                    ws.Left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                    ws.Top  + 40  < SystemParameters.VirtualScreenTop  + SystemParameters.VirtualScreenHeight;

                Width  = ws.Width;
                Height = ws.Height;
                if (onScreen)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = ws.Left;
                    Top  = ws.Top;
                }
                return true;
            }
            catch { return false; }
        }

        private void SaveWindowSettings()
        {
            try
            {
                var b = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, ActualWidth, ActualHeight)
                    : RestoreBounds;
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                    new WindowSettings { Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
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
            if (sender is not FrameworkElement fe || fe.Tag is not SongCard card) return;
            if (card.AssignedWav != null) TogglePlay(card);   // has a song → play / pause
            else PickFileFor(card);                           // empty → choose a song
        }

        private void PickFileFor(SongCard card)
        {
            if (_dialogOpen) return;
            _dialogOpen = true;
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Filter      = WemEncoder.FileFilter,
                    Multiselect = false,
                    Title       = $"Select audio for: {card.DisplayName}"
                };
                if (ofd.ShowDialog() == true) Assign(card, ofd.FileName);
            }
            finally { _dialogOpen = false; }
        }

        private void Assign(SongCard card, string? file)
        {
            if (_nowPlaying == card) StopPlayback();
            card.AssignedWav = file;
            if (file != null) Log($"Assigned: {Path.GetFileName(file)}  →  {card.WemName}  ({card.DisplayName})");
            else              Log($"Removed song from {card.DisplayName}");
        }

        private static SongCard? CardOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as SongCard;

        private void CardRemove_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (CardOf(sender) is SongCard c) Assign(c, null);
        }

        private void CardMenu_Play(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is not SongCard c) return;
            if (c.AssignedWav != null) TogglePlay(c); else PickFileFor(c);
        }

        private void CardMenu_Replace(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is SongCard c) PickFileFor(c);
        }

        private void CardMenu_Remove(object sender, RoutedEventArgs e)
        {
            if (CardOf(sender) is SongCard c && c.AssignedWav != null) Assign(c, null);
        }

        private void Card_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop)) { e.Effects = WpfDragEffects.None; return; }
            var files   = (string[])e.Data.GetData(WpfDataFormats.FileDrop);
            bool hasWav = files.Any(WemEncoder.IsSupported);
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
            if (sender is not FrameworkElement fe || fe.Tag is not SongCard card) return;
            card.IsDragOver = false;
            if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(WpfDataFormats.FileDrop);
            string? wav = files.FirstOrDefault(WemEncoder.IsSupported);
            if (wav == null) { Log("Drop ignored — not a supported audio file."); return; }
            Assign(card, wav);
        }

        // ── Preview player ────────────────────────────────────────────────
        private void TogglePlay(SongCard card)
        {
            if (card.AssignedWav == null) return;

            // Same card: pause / resume
            if (_nowPlaying == card && _player != null)
            {
                if (_player.PlaybackState == PlaybackState.Playing) { _player.Pause(); card.IsPlaying = false; }
                else                                                { _player.Play();  card.IsPlaying = true;  }
                return;
            }

            // Different card: stop the old one, start this one
            StopPlayback();
            try
            {
                _playerReader = new AudioFileReader(card.AssignedWav) { Volume = 0.8f };
                _player       = new WaveOutEvent();
                _player.Init(_playerReader);
                _player.PlaybackStopped += Player_PlaybackStopped;
                _player.Play();

                _nowPlaying     = card;
                card.Progress   = 0;
                card.IsPlaying  = true;

                if (_progressTimer == null)
                {
                    _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    _progressTimer.Tick += (s, e) =>
                    {
                        if (_nowPlaying == null || _playerReader == null) return;
                        double total = _playerReader.TotalTime.TotalSeconds;
                        if (total > 0) _nowPlaying.Progress = _playerReader.CurrentTime.TotalSeconds / total;
                    };
                }
                _progressTimer.Start();
            }
            catch (Exception ex)
            {
                Log($"Can't play {card.AssignedFileName}: {ex.Message}");
                StopPlayback();
            }
        }

        private void Player_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            // Fires on the UI thread when the song reaches the end
            if (!ReferenceEquals(sender, _player)) return;
            if (e.Exception != null) Log($"Playback error: {e.Exception.Message}");
            StopPlayback();
        }

        private void StopPlayback()
        {
            _progressTimer?.Stop();
            var p = _player;
            _player = null;
            if (p != null)
            {
                p.PlaybackStopped -= Player_PlaybackStopped;
                try { p.Stop(); } catch { }
                p.Dispose();
            }
            _playerReader?.Dispose();
            _playerReader = null;
            if (_nowPlaying != null)
            {
                _nowPlaying.IsPlaying = false;
                _nowPlaying.Progress  = 0;
                _nowPlaying = null;
            }
        }

        // ── Search / filter ───────────────────────────────────────────────
        private bool FilterCard(SongCard c)
        {
            if (_search.Length > 0 &&
                !(c.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                  c.WemName.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                  c.AssignedFileName.Contains(_search, StringComparison.OrdinalIgnoreCase)))
                return false;

            return _filterMode switch
            {
                "assigned" => c.AssignedWav != null,
                "nte"      => NteImages.Contains(c.ImageName ?? "ost.png"),
                "persona"  => PersonaImages.Contains(c.ImageName ?? ""),
                "tof"      => HTImages.Contains(c.ImageName ?? ""),
                _          => true
            };
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            _search = TxtSearch.Text.Trim();
            if (TxtSearchHint != null)
                TxtSearchHint.Visibility = TxtSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view?.Refresh();
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string mode)
            {
                _filterMode = mode;
                _view?.Refresh();
            }
        }

        private void UpdateCounts()
        {
            int n = cards.Count(c => c.AssignedWav != null);
            TxtSubtitle.Text = n == 0
                ? $"{cards.Count} songs · click a card to add audio"
                : $"{n} of {cards.Count} songs swapped";
            BtnBuild.Content = n > 0 ? $"BUILD ({n})" : "BUILD";
        }

        // ── Title bar buttons ─────────────────────────────────────────────
        private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void LogToggle_Click(object sender, RoutedEventArgs e) =>
            LogPanel.Visibility = LogPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

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
                Title       = $"Pick audio files for [{label}] slots",
                Filter      = WemEncoder.FileFilter,
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
            StopPlayback();
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
            StopPlayback();
            foreach (var c in cards) c.AssignedWav = null;
            Log("All assignments cleared.");
        }

        private async void BuildMod_Click(object sender, RoutedEventArgs e)
        {
            var assigned = cards.Where(c => c.AssignedWav != null).ToList();
            if (assigned.Count == 0)
            {
                WpfMessageBox.Show("No songs assigned!\n\nClick or drag an audio file onto at least one song card.",
                    "NTE Audio Swapper", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!File.Exists(wav2wemPath))
            {
                WpfMessageBox.Show("wav2wem.exe is missing from the app's resources.\n\nMake sure Resources\\wav2wem.exe is set as an Embedded Resource.",
                    "Encoder Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            StopPlayback();
            LogPanel.Visibility = Visibility.Visible;
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

                Log("Step 1: Converting audio → WEM...");
                int i = 0;
                foreach (var c in assigned)
                {
                    i++;
                    BtnBuild.Content = $"{i}/{assigned.Count}...";
                    string wemNoExt = Path.GetFileNameWithoutExtension(c.WemName);
                    string normWav  = Path.Combine(audioWorkDir, wemNoExt + ".wav");
                    string outWem   = Path.Combine(wemOutDir, c.WemName);
                    Log($"  {Path.GetFileName(c.AssignedWav)} → {c.WemName}");
                    await Task.Run(() => WemEncoder.NormalizeToWav(c.AssignedWav!, normWav));
                    int code = await RunProcessAsync(wav2wemPath,
                        $"\"{normWav}\" -o \"{outWem}\" -q {VorbisQuality}");
                    if (code != 0) throw new Exception($"wav2wem failed (exit {code}) for: {c.WemName}");
                }

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
                int upakCode = await RunProcessAsync(upakExePath, $"\"{outputPak}\" -create=\"{pakResponsePath}\"");
                if (!File.Exists(outputPak)) throw new Exception($"UnrealPak finished (exit {upakCode}) but pak was not created.");

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
                UpdateCounts();
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
                upakExePath = Path.Combine(resourcesDir, "UnrealPak.exe");
                wav2wemPath = Path.Combine(resourcesDir, "wav2wem.exe");
                Log($"Resources ready. wav2wem={File.Exists(wav2wemPath)} upak={File.Exists(upakExePath)}");
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Failed to extract resources:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private Task<int> RunProcessAsync(string exe, string args)
        {
            var tcs = new TaskCompletionSource<int>();
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
            p.Exited += (s, e) => { p.WaitForExit(); tcs.TrySetResult(p.ExitCode); p.Dispose(); };
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
            StopPlayback();
            StopBoundsAnimation();
            SaveWindowSettings();
            try { Directory.Delete(resourcesDir, true); } catch { }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
