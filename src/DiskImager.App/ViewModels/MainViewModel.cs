using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskImagerX.Disk;
using DiskImagerX.Engine;

namespace DiskImagerX.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDiskBackend _backend = BackendFactory.Create();
    private CancellationTokenSource? _cts;

    // Host hooks (set by the view) for file pickers and confirmations.
    public Func<bool, string, Task<string?>>? PickFile;         // (save?, suggestedName) -> path
    public Func<string, string, string, Task<bool>>? Confirm;   // (title, message, danger action) -> ok
    public Func<string, Task>? Info;                            // simple message

    public MainViewModel()
    {
        Platform = _backend.PlatformName;
        IsElevated = _backend.IsElevated();
        ElevationText = IsElevated ? "elevated" : _backend.ElevationHint;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText = v is null ? "" : $"v{v.Major}.{v.Minor}";
        _ = RefreshAsync();
    }

    // ── header / status ───────────────────────────────────────────────────────
    [ObservableProperty] private string _platform = "";
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _elevationText = "";

    // ── mode (0 backup · 1 restore · 2 verify · 3 format · 4 mount) ────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackup), nameof(IsRestore), nameof(IsVerify), nameof(IsFormat), nameof(IsMount))]
    [NotifyPropertyChangedFor(nameof(ActionLabel), nameof(NeedsDisk), nameof(NeedsFile), nameof(SectionTitle))]
    private int _mode;

    public bool IsBackup => Mode == 0;
    public bool IsRestore => Mode == 1;
    public bool IsVerify => Mode == 2;
    public bool IsFormat => Mode == 3;
    public bool IsMount => Mode == 4;
    public bool NeedsDisk => Mode != 4;
    public bool NeedsFile => Mode != 3;   // format has no file
    public string SectionTitle => Mode switch { 1 => "TARGET DISK  (WILL BE ERASED)", 2 => "DISK TO VERIFY", 3 => "TARGET DISK  (WILL BE FORMATTED)", 4 => "IMAGE TO MOUNT", _ => "SOURCE DISK" };
    public string ActionLabel => Mode switch { 1 => "WRITE TO DISK", 2 => "VERIFY", 3 => "FORMAT DISK", 4 => "MOUNT IMAGE", _ => "START BACKUP" };

    [RelayCommand] private void SetMode(string m) { if (int.TryParse(m, out var i)) Mode = i; }

    // ── disks ─────────────────────────────────────────────────────────────────
    public ObservableCollection<DiskInfo> Disks { get; } = new();
    [ObservableProperty] private DiskInfo? _selectedDisk;
    [ObservableProperty] private string _diskInfoText = "";

    partial void OnSelectedDiskChanged(DiskInfo? value)
        => DiskInfoText = value is null ? "" : $"{value.DevicePath}   |   {value.SizeText}   |   {value.Model}";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        StatusText = "Scanning…";
        try
        {
            var disks = await _backend.EnumerateAsync();
            Disks.Clear();
            foreach (var d in disks) Disks.Add(d);
            SelectedDisk = Disks.FirstOrDefault();
            StatusText = Disks.Count == 0
                ? (IsElevated ? "No disks detected. Connect a drive and Refresh." : $"No disks — {_backend.ElevationHint}")
                : "Select a disk and a file, then Start.";
        }
        catch (Exception ex) { StatusText = "Scan error: " + ex.Message; }
    }

    // ── path + options ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _formatText = "";
    [ObservableProperty] private bool _gzip;
    [ObservableProperty] private bool _sha256;
    [ObservableProperty] private bool _smartRestore = true;
    [ObservableProperty] private bool _verifyAfter = true;
    [ObservableProperty] private string _volumeLabel = "";
    [ObservableProperty] private bool _quickFormat = true;

    partial void OnPathChanged(string value)
        => FormatText = (Mode is 1 or 3) && System.IO.File.Exists(value) ? ImageSource.Detect(value) : "";

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFile is null) return;
        bool save = Mode == 0;
        string suggested = Mode == 0 ? (Gzip ? "backup.img.gz" : "backup.img") : "";
        var p = await PickFile(save, suggested);
        if (!string.IsNullOrEmpty(p)) Path = p;
    }

    // ── progress ────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _percentText = "READY";

    private void OnProgress(ImagingProgress p)
    {
        double pct = p.Percent;
        IsIndeterminate = pct < 0 && p.Phase is Phase.Backing or Phase.Restoring or Phase.Verifying;
        ProgressPercent = pct < 0 ? 0 : pct;
        PercentText = pct < 0 ? "…" : $"{pct:0.0}%";
        SpeedText = p.MBps > 0.01 ? $"{p.MBps:0.0} MB/s" : "";
        StatusText = p.Message;
        if (p.Phase is Phase.Done)
        { IsRunning = false; PercentText = "DONE"; ProgressPercent = 100; IsIndeterminate = false; }
        else if (p.Phase is Phase.Cancelled or Phase.Error)
        { IsRunning = false; PercentText = "READY"; ProgressPercent = 0; IsIndeterminate = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        if (Mode == 4) { await MountAsync(); return; }

        var disk = SelectedDisk;
        if (disk is null) { await (Info?.Invoke("Select a disk first.") ?? Task.CompletedTask); return; }
        if (NeedsFile && string.IsNullOrWhiteSpace(Path)) { await (Info?.Invoke("Choose a file path first.") ?? Task.CompletedTask); return; }
        if (!IsElevated) { await (Info?.Invoke(_backend.ElevationHint) ?? Task.CompletedTask); return; }

        // Destructive confirmation for restore + format.
        if (Mode is 1 or 3)
        {
            if (Mode == 1 && !System.IO.File.Exists(Path)) { await (Info?.Invoke("Image file not found.") ?? Task.CompletedTask); return; }
            string verb = Mode == 3 ? "Format" : "Write to";
            string title = disk.IsSystem ? $"{verb} the SYSTEM disk?" : $"{verb} this disk?";
            string msg = $"{disk.DevicePath}\n{disk.SizeText} — {disk.Model}\n\nEVERYTHING on it will be erased. This cannot be undone.";
            bool ok = await (Confirm?.Invoke(title, msg, disk.IsSystem ? "ERASE" : verb) ?? Task.FromResult(false));
            if (!ok) return;
        }

        var progress = new Progress<ImagingProgress>(OnProgress);
        _cts = new CancellationTokenSource();
        IsRunning = true; PercentText = "…"; SpeedText = ""; ProgressPercent = 0;
        try
        {
            await Task.Run(() =>
            {
                switch (Mode)
                {
                    case 0: Imaging.Backup(_backend, disk, Path, Gzip, Sha256, progress, _cts.Token); break;
                    case 1: Imaging.Restore(_backend, disk, Path, SmartRestore, VerifyAfter, progress, _cts.Token); break;
                    case 2: Imaging.Verify(_backend, disk, Path, SmartRestore, progress, _cts.Token); break;
                    case 3: Fat32.Format(_backend, disk, VolumeLabel, QuickFormat, progress, _cts.Token); break;
                }
            });
        }
        catch (OperationCanceledException) { OnProgress(new ImagingProgress(Phase.Cancelled, 0, 0, 0, TimeSpan.Zero, "Cancelled.")); }
        catch (Exception ex) { OnProgress(new ImagingProgress(Phase.Error, 0, 0, 0, TimeSpan.Zero, "Error: " + ex.Message)); }
        finally { IsRunning = false; _cts?.Dispose(); _cts = null; }
    }

    private async Task MountAsync()
    {
        if (string.IsNullOrWhiteSpace(Path) || !System.IO.File.Exists(Path)) { await (Info?.Invoke("Select an image to mount.") ?? Task.CompletedTask); return; }
        IsRunning = true; StatusText = "Mounting…";
        try { var loc = await _backend.MountImageAsync(Path); StatusText = "Mounted: " + (loc ?? "OK"); }
        catch (Exception ex) { StatusText = "Mount error: " + ex.Message; }
        finally { IsRunning = false; }
    }
}
