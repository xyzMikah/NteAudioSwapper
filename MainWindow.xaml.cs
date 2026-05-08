using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace NtePakTool
{
    public class QueueItem
    {
        public string SourcePath { get; set; } = "";
        public string TargetWem { get; set; } = "";
        public string SourceFileName => System.IO.Path.GetFileName(SourcePath);
    }

    public partial class MainWindow : Window
    {
        private string wwiseConsolePath = "";
        private string wprojPath = "";
        private string upakExePath = "";
        private readonly string resourcesDir = Path.Combine(Path.GetTempPath(), "NTEModBuilder_Resources");
        private readonly ObservableCollection<QueueItem> queue = new();

        public MainWindow()
        {
            InitializeComponent();
            FileQueue.ItemsSource = queue;
            ExtractEmbeddedResources();
            wwiseConsolePath = DetectWwiseConsolePath();
            UpdateWwiseStatus();
        }

        // ── Extract embedded resources ───────────────────────────────────

        private void ExtractEmbeddedResources()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resources = assembly.GetManifestResourceNames();

                foreach (var resource in resources)
                {
                    if (!resource.StartsWith("NtePakTool.Resources.")) continue;

                    string relative = resource.Substring("NtePakTool.Resources.".Length);

                    relative = relative
                        .Replace("WwiseProject.Conversion_Settings.", "WwiseProject\\Conversion Settings\\")
                        .Replace("WwiseProject.", "WwiseProject\\");

                    int lastDot = relative.LastIndexOf('.');
                    string outPath;
                    if (lastDot >= 0)
                    {
                        string ext = relative.Substring(lastDot);
                        string nameNoExt = relative.Substring(0, lastDot).Replace('.', '\\');
                        outPath = Path.Combine(resourcesDir, nameNoExt + ext);
                    }
                    else
                    {
                        outPath = Path.Combine(resourcesDir, relative);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                    using var stream = assembly.GetManifestResourceStream(resource)!;
                    using var file = File.Create(outPath);
                    stream.CopyTo(file);
                }

                wprojPath = Path.Combine(resourcesDir, "WwiseProject", "NTE.wproj");
                upakExePath = Path.Combine(resourcesDir, "UnrealPak.exe");

                Log($"Resources ready. wproj={File.Exists(wprojPath)} upak={File.Exists(upakExePath)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to extract resources:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Wwise detection ──────────────────────────────────────────────

        private string DetectWwiseConsolePath()
        {
            const string subPath = @"Authoring\x64\Release\bin\WwiseConsole.exe";

            string? wwiseRoot = Environment.GetEnvironmentVariable("WWISEROOT");
            if (!string.IsNullOrEmpty(wwiseRoot))
            {
                string path = Path.Combine(wwiseRoot.Trim('"'), subPath);
                if (File.Exists(path)) return path;
            }

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                foreach (var baseDir in new[] { "Audiokinetic", @"Program Files\Audiokinetic", @"Program Files (x86)\Audiokinetic" })
                {
                    string searchDir = Path.Combine(drive.Name, baseDir);
                    if (!Directory.Exists(searchDir)) continue;
                    try
                    {
                        foreach (var dir in Directory.GetDirectories(searchDir, "*Wwise*", SearchOption.TopDirectoryOnly))
                        {
                            string path = Path.Combine(dir, subPath);
                            if (File.Exists(path)) return path;
                        }
                    }
                    catch { }
                }
            }

            return "";
        }

        private void UpdateWwiseStatus()
        {
            if (File.Exists(wwiseConsolePath))
            {
                TxtWwiseStatus.Text = "wwise ready";
                TxtWwiseStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x46, 0xF0, 0xD2));
            }
            else
            {
                TxtWwiseStatus.Text = "wwise not found — install 2025.1.7.9143";
                TxtWwiseStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x66));
            }
        }

        // ── Drag and drop ────────────────────────────────────────────────

        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            AddFiles(files);
        }

        private void DropZone_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            Browse_Click(sender, new RoutedEventArgs());
        }

        private void UpdateHint()
        {
            DropHint.Visibility = queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "WAV Files|*.wav",
                Multiselect = true
            };
            if (ofd.ShowDialog() == true) AddFiles(ofd.FileNames);
        }

        private void AddFiles(string[] files)
        {
            foreach (var f in files)
            {
                if (!f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Skipped (not a wav): {Path.GetFileName(f)}");
                    continue;
                }

                string targetWem = Path.GetFileNameWithoutExtension(f) + ".wem";

                if (queue.Any(q => q.TargetWem == targetWem))
                {
                    Log($"Already in queue: {targetWem}");
                    continue;
                }

                queue.Add(new QueueItem { SourcePath = f, TargetWem = targetWem });
                Log($"Added: {targetWem}");
            }
            UpdateHint();
        }

        // ── Queue controls ───────────────────────────────────────────────

        private void Clear_Click(object sender, RoutedEventArgs e) { queue.Clear(); UpdateHint(); }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = FileQueue.SelectedItems.Cast<QueueItem>().ToList();
            foreach (var item in selected) queue.Remove(item);
            UpdateHint();
        }

        // ── Build ────────────────────────────────────────────────────────

        private async void BuildMod_Click(object sender, RoutedEventArgs e)
        {
            if (queue.Count == 0)
            {
                MessageBox.Show("No files in queue!", "NTE Mod Builder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(wwiseConsolePath))
            {
                try { Process.Start(new ProcessStartInfo("https://www.audiokinetic.com/en/download-launcher/?platform=1") { UseShellExecute = true }); } catch { }
                MessageBox.Show("WwiseConsole.exe not found.\n\nThe download page has been opened.\nPlease install Wwise version 2025.1.7.9143.",
                    "Wwise Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnBuild.IsEnabled = false;
            string modName = string.IsNullOrWhiteSpace(TxtModName.Text) ? "AudioMod_P" : TxtModName.Text.Trim();

            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string outputFolder = Path.Combine(desktopPath, modName);
            string outputPak = Path.Combine(outputFolder, modName + ".pak");

            string tempDir = Path.Combine(Path.GetTempPath(), "NTEAudioModBuilder_" + DateTime.Now.Ticks);
            string audioWorkDir = Path.Combine(tempDir, "AudioWork");
            string wemOutDir = Path.Combine(tempDir, "WemOut");

            try
            {
                Directory.CreateDirectory(audioWorkDir);
                Directory.CreateDirectory(wemOutDir);
                Directory.CreateDirectory(outputFolder);

                // ── Step 1: Convert WAV → WEM ──────────────────────────
                Log($"Starting build: {queue.Count} file(s)...");
                Log("Step 1: Converting WAV → WEM via Wwise...");

                var xml = new StringBuilder();
                xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                xml.AppendLine($"<ExternalSourcesList SchemaVersion=\"1\" Root=\"{audioWorkDir}\">");

                foreach (var item in queue)
                {
                    string destWav = Path.Combine(audioWorkDir, Path.GetFileNameWithoutExtension(item.TargetWem) + ".wav");
                    File.Copy(item.SourcePath, destWav, true);
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

                foreach (var item in queue)
                {
                    string expected = Path.Combine(wemOutDir, item.TargetWem);
                    if (!File.Exists(expected))
                        throw new Exception($"WEM conversion failed for: {item.TargetWem}");
                }
                Log("WEM conversion complete.");

                // ── Step 2: Build response file ─────────────────────────
                Log("Step 2: Staging pak folder structure...");
                string pakResponsePath = Path.Combine(tempDir, "pak_response.txt");
                using (var sw = new StreamWriter(pakResponsePath, false, new UTF8Encoding(false)))
                {
                    foreach (var item in queue)
                    {
                        string wemFile = Path.Combine(wemOutDir, item.TargetWem);
                        string inPakPath = $"../../../HT/Content/WwiseAudio/Media/{item.TargetWem}";
                        sw.WriteLine($"\"{wemFile}\" \"{inPakPath}\"");
                    }
                }

                // ── Step 3: Pack with embedded UnrealPak ─────────────────
                Log("Step 3: Packing into .pak...");
                if (!File.Exists(upakExePath))
                    throw new Exception("UnrealPak.exe not found in extracted resources.");

                await RunProcessAsync(upakExePath,
                    $"\"{outputPak}\" -create=\"{pakResponsePath}\"");

                if (!File.Exists(outputPak))
                    throw new Exception("UnrealPak finished but output pak was not created.");

                // ── Copy dummy files with mod name ──────────────────────
                Log("Copying dummy files...");
                var assembly = Assembly.GetExecutingAssembly();
                foreach (var ext in new[] { "sig", "ucas", "utoc" })
                {
                    string resourceName = $"NtePakTool.Resources.dummy.{ext}";
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        string destFile = Path.Combine(outputFolder, modName + "." + ext);
                        using var file = File.Create(destFile);
                        stream.CopyTo(file);
                        Log($"Copied dummy.{ext} → {modName}.{ext}");
                    }
                    else
                    {
                        Log($"WARNING: dummy.{ext} not found in resources!");
                    }
                }

                Log($"Done! Saved to: {outputFolder}");
                MessageBox.Show($"Mod built successfully!\n\nFolder saved to Desktop:\n{modName}",
                    "Build Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                MessageBox.Show($"Build failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnBuild.IsEnabled = true;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private Task RunProcessAsync(string exe, string args)
        {
            var tcs = new TaskCompletionSource<bool>();
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
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
            try { Directory.Delete(resourcesDir, true); } catch { }
        }

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
