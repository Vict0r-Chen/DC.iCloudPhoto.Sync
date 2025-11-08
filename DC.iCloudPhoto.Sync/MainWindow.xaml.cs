using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DC.iCloudPhoto.Sync;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const long SizeToleranceBytes = 1024;
    private const double TimeToleranceSeconds = 1d;
    private const int DefaultPageSize = 50;
    private const int MaxLogLines = 500;
    private static readonly string SettingsFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "settings.json");
    private static readonly string LogDirectory = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "logs");
    private static readonly string LogFilePath = Path.Combine(
        LogDirectory, $"log_{DateTime.Now:yyyyMMdd}.txt");

    private readonly List<FileSyncItem> _allPreviewItems = new();
    private readonly List<FileSyncItem> _filteredPreviewItems = new();
    private readonly ObservableCollection<FileSyncItem> _pagedItems = new();
    private readonly StringBuilder _logBuilder = new();
    private readonly Queue<string> _logQueue = new();

    private string _sourceFolder = @"E:\Photos";
    private string _targetFolder = ResolveDefaultTargetPath();
    private DateTime? _startDate;
    private DateTime? _endDate;
    private bool _includeJpg = true;
    private bool _includeMp4 = true;
    private bool _includeVideoMov = true;
    private int _pageSize = DefaultPageSize;
    private int _currentPage = 1;
    private bool _isBusy;
    private string _logText = string.Empty;
    private bool _showPendingCopy = true;
    private bool _showUpToDate = false;
    private bool _showTargetOnly = true;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        InitializeLogging();
        LoadSettings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FileSyncItem> PagedItems => _pagedItems;

    public IReadOnlyList<int> PageSizeOptions { get; } = new[] { 25, 50, 100, 200 };

    public string SourceFolder
    {
        get => _sourceFolder;
        set
        {
            if (SetProperty(ref _sourceFolder, value))
            {
                SaveSettings();
            }
        }
    }

    public string TargetFolder
    {
        get => _targetFolder;
        set
        {
            if (SetProperty(ref _targetFolder, value))
            {
                SaveSettings();
            }
        }
    }

    public DateTime? StartDate
    {
        get => _startDate;
        set
        {
            if (SetProperty(ref _startDate, value))
            {
                SaveSettings();
            }
        }
    }

    public DateTime? EndDate
    {
        get => _endDate;
        set
        {
            if (SetProperty(ref _endDate, value))
            {
                SaveSettings();
            }
        }
    }

    public bool IncludeJpg
    {
        get => _includeJpg;
        set
        {
            if (SetProperty(ref _includeJpg, value))
            {
                OnPropertyChanged(nameof(CanSync));
                SaveSettings();
            }
        }
    }

    public bool IncludeMp4
    {
        get => _includeMp4;
        set
        {
            if (SetProperty(ref _includeMp4, value))
            {
                OnPropertyChanged(nameof(CanSync));
                SaveSettings();
            }
        }
    }

    public bool IncludeVideoMov
    {
        get => _includeVideoMov;
        set
        {
            if (SetProperty(ref _includeVideoMov, value))
            {
                OnPropertyChanged(nameof(CanSync));
                SaveSettings();
            }
        }
    }

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (value <= 0)
            {
                value = DefaultPageSize;
            }

            if (SetProperty(ref _pageSize, value))
            {
                RefreshPagination();
            }
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            var clamped = Math.Max(1, Math.Min(value, TotalPages));
            if (SetProperty(ref _currentPage, clamped))
            {
                OnPropertyChanged(nameof(PageSummary));
            }
        }
    }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)_filteredPreviewItems.Count / PageSize));

    public string PageSummary => $"第 {CurrentPage} 页 / 共 {TotalPages} 页（{_filteredPreviewItems.Count} 条）";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanSync));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public bool CanSync => !IsBusy && _allPreviewItems.Any(i => i.Status == SyncStatus.PendingCopy);

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public bool ShowPendingCopy
    {
        get => _showPendingCopy;
        set
        {
            if (SetProperty(ref _showPendingCopy, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool ShowUpToDate
    {
        get => _showUpToDate;
        set
        {
            if (SetProperty(ref _showUpToDate, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool ShowTargetOnly
    {
        get => _showTargetOnly;
        set
        {
            if (SetProperty(ref _showTargetOnly, value))
            {
                ApplyFilter();
            }
        }
    }

    public int PendingCopyCount => _allPreviewItems.Count(i => i.Status == SyncStatus.PendingCopy);
    public int UpToDateCount => _allPreviewItems.Count(i => i.Status == SyncStatus.UpToDate);
    public int TargetOnlyCount => _allPreviewItems.Count(i => i.Status == SyncStatus.TargetOnly);
    public int TotalItemsCount => _allPreviewItems.Count;
    public string StatisticsSummary => $"共 {TotalItemsCount} 项：等待同步 {PendingCopyCount}，已存在 {UpToDateCount}，仅目标存在 {TargetOnlyCount}";

    private async void OnPreviewClicked(object sender, RoutedEventArgs e)
    {
        await RunBusyTask(async () =>
        {
            var options = BuildOptions();
            if (options == null)
            {
                return;
            }

            AppendLog("========================================");
            AppendLog("开始预览");

            // 记录时间范围
            var timeRangeInfo = "时间范围：";
            if (options.StartUtc.HasValue && options.EndUtc.HasValue)
            {
                timeRangeInfo += $"{options.StartUtc.Value.ToLocalTime():yyyy-MM-dd} 至 {options.EndUtc.Value.ToLocalTime():yyyy-MM-dd}";
            }
            else if (options.StartUtc.HasValue)
            {
                timeRangeInfo += $"{options.StartUtc.Value.ToLocalTime():yyyy-MM-dd} 起";
            }
            else if (options.EndUtc.HasValue)
            {
                timeRangeInfo += $"至 {options.EndUtc.Value.ToLocalTime():yyyy-MM-dd}";
            }
            else
            {
                timeRangeInfo += "全部";
            }
            AppendLog(timeRangeInfo);

            // 记录文件类型
            var extensions = options.Extensions != null ? string.Join(", ", options.Extensions) : "全部";
            AppendLog($"文件类型：{extensions}");

            AppendLog("开始扫描源文件夹和目标文件夹...");
            var previewItems = await Task.Run(() => BuildPreviewInternal(options, AppendLog));
            ApplyPreview(previewItems);

            // 记录统计信息
            var pendingCount = previewItems.Count(i => i.Status == SyncStatus.PendingCopy);
            var upToDateCount = previewItems.Count(i => i.Status == SyncStatus.UpToDate);
            var targetOnlyCount = previewItems.Count(i => i.Status == SyncStatus.TargetOnly);

            AppendLog($"预览完成：共 {previewItems.Count} 项");
            AppendLog($"  - 等待同步：{pendingCount} 项");
            AppendLog($"  - 已存在：{upToDateCount} 项");
            AppendLog($"  - 仅目标存在：{targetOnlyCount} 项");
            AppendLog("========================================");
        });
    }

    private async void OnSyncClicked(object sender, RoutedEventArgs e)
    {
        await RunBusyTask(async () =>
        {
            if (!_allPreviewItems.Any(i => i.Status == SyncStatus.PendingCopy))
            {
                AppendLog("没有需要同步的文件。");
                return;
            }

            var options = BuildOptions();
            if (options == null)
            {
                return;
            }

            var pendingItems = _allPreviewItems.Where(i => i.Status == SyncStatus.PendingCopy).ToList();
            AppendLog($"开始同步，共 {pendingItems.Count} 个文件等待处理...");

            int success = 0;
            await Task.Run(() =>
            {
                foreach (var item in pendingItems)
                {
                    if (item.SourcePath == null || item.PlannedTargetPath == null)
                    {
                        continue;
                    }

                    try
                    {
                        var destinationPath = EnsureDestinationPath(options.TargetFolder, item);
                        var directory = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        if (string.Equals(item.SourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                        {
                            AppendLog($"跳过（源与目标相同）：{item.FileName}");
                            continue;
                        }

                        File.Copy(item.SourcePath, destinationPath, true);
                        var targetInfo = new FileInfo(destinationPath);
                        Dispatcher.Invoke(() =>
                        {
                            item.TargetPath = destinationPath;
                            item.TargetSize = targetInfo.Length;
                            item.TargetModified = targetInfo.LastWriteTime;
                            item.Status = SyncStatus.UpToDate;
                        });

                        success++;
                        AppendLog($"同步成功：{item.FileName}");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"同步失败（{item.FileName}）：{ex.Message}");
                    }
                }
            });

            RefreshPagination();
            AppendLog($"同步完成，成功 {success}/{pendingItems.Count}。");
        });
    }

    private async Task RunBusyTask(Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await operation();
        }
        catch (Exception ex)
        {
            AppendLog($"操作失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            RefreshPagination();
        }
    }

    private void OnPrevPageClicked(object sender, RoutedEventArgs e)
    {
        if (CurrentPage <= 1)
        {
            return;
        }

        CurrentPage -= 1;
        RefreshPagination();
    }

    private void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        if (CurrentPage >= TotalPages)
        {
            return;
        }

        CurrentPage += 1;
        RefreshPagination();
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        _logBuilder.Clear();
        _logQueue.Clear();
        LogText = string.Empty;
    }

    private void OnOpenSourceFolderClicked(object sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        var contextMenu = menuItem?.Parent as ContextMenu;
        var dataGrid = contextMenu?.PlacementTarget as DataGrid;
        var item = dataGrid?.SelectedItem as FileSyncItem;

        if (item == null || string.IsNullOrWhiteSpace(item.SourcePath))
        {
            MessageBox.Show(this, "源文件不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenFileInExplorer(item.SourcePath);
    }

    private void OnOpenTargetFolderClicked(object sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        var contextMenu = menuItem?.Parent as ContextMenu;
        var dataGrid = contextMenu?.PlacementTarget as DataGrid;
        var item = dataGrid?.SelectedItem as FileSyncItem;

        if (item == null || string.IsNullOrWhiteSpace(item.TargetPath))
        {
            MessageBox.Show(this, "目标文件不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenFileInExplorer(item.TargetPath);
    }

    private void OpenFileInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else
            {
                MessageBox.Show(this, "文件不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开文件夹：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyPreview(IReadOnlyList<FileSyncItem> items)
    {
        _allPreviewItems.Clear();
        _allPreviewItems.AddRange(items);
        OnPropertyChanged(nameof(PendingCopyCount));
        OnPropertyChanged(nameof(UpToDateCount));
        OnPropertyChanged(nameof(TargetOnlyCount));
        OnPropertyChanged(nameof(TotalItemsCount));
        OnPropertyChanged(nameof(StatisticsSummary));
        CurrentPage = 1;
        ApplyFilter();
        OnPropertyChanged(nameof(CanSync));
    }

    private void ApplyFilter()
    {
        _filteredPreviewItems.Clear();

        foreach (var item in _allPreviewItems)
        {
            bool shouldShow = item.Status switch
            {
                SyncStatus.PendingCopy => ShowPendingCopy,
                SyncStatus.UpToDate => ShowUpToDate,
                SyncStatus.TargetOnly => ShowTargetOnly,
                _ => false
            };

            if (shouldShow)
            {
                _filteredPreviewItems.Add(item);
            }
        }

        RefreshPagination();
    }

    private void RefreshPagination()
    {
        _pagedItems.Clear();
        var desiredPage = _filteredPreviewItems.Count == 0
            ? 1
            : Math.Max(1, Math.Min(CurrentPage, TotalPages));
        CurrentPage = desiredPage;

        var skip = (CurrentPage - 1) * PageSize;
        foreach (var item in _filteredPreviewItems.Skip(skip).Take(PageSize))
        {
            _pagedItems.Add(item);
        }

        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(CanSync));
    }

    private SyncOptions? BuildOptions()
    {
        if (string.IsNullOrWhiteSpace(SourceFolder))
        {
            MessageBox.Show(this, "请填写源文件夹路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        if (string.IsNullOrWhiteSpace(TargetFolder))
        {
            MessageBox.Show(this, "请填写目标文件夹路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        string source;
        string target;
        try
        {
            source = NormalizePath(SourceFolder);
            target = NormalizePath(TargetFolder);
        }
        catch (Exception ex)
        {
            AppendLog($"路径解析失败：{ex.Message}");
            MessageBox.Show(this, "文件夹路径无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (!Directory.Exists(source))
        {
            AppendLog($"源文件夹不存在：{source}");
            MessageBox.Show(this, "源文件夹不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (!Directory.Exists(target))
        {
            AppendLog($"目标文件夹不存在：{target}");
            MessageBox.Show(this, "目标文件夹不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (StartDate.HasValue && EndDate.HasValue && StartDate > EndDate)
        {
            MessageBox.Show(this, "开始时间不能晚于结束时间。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var extensionSet = BuildExtensionSet();
        var startLocal = StartDate?.Date;
        var endLocal = EndDate?.Date.AddDays(1).AddTicks(-1);

        return new SyncOptions
        {
            SourceFolder = source,
            TargetFolder = target,
            StartUtc = startLocal != null ? DateTime.SpecifyKind(startLocal.Value, DateTimeKind.Local).ToUniversalTime() : null,
            EndUtc = endLocal != null ? DateTime.SpecifyKind(endLocal.Value, DateTimeKind.Local).ToUniversalTime() : null,
            Extensions = extensionSet
        };
    }

    private HashSet<string>? BuildExtensionSet()
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IncludeJpg)
        {
            extensions.Add(".jpg");
            extensions.Add(".jpeg");
        }

        if (IncludeMp4)
        {
            extensions.Add(".mp4");
        }

        if (IncludeVideoMov)
        {
            extensions.Add(".mov");
        }

        return extensions.Count > 0 ? extensions : null;
    }

    private static List<FileSyncItem> BuildPreviewInternal(SyncOptions options, Action<string> log)
    {
        var sourceFiles = EnumerateFiles(options.SourceFolder, options.Extensions, options.StartUtc, options.EndUtc, log, "源");
        var targetFiles = EnumerateFiles(options.TargetFolder, options.Extensions, null, null, log, "目标");

        var targetByName = targetFiles.GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                                      .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var targetByBucket = BuildSizeBuckets(targetFiles);
        var matchedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<FileSyncItem>();

        foreach (var source in sourceFiles)
        {
            FileMetadata? matched = null;
            if (targetByName.TryGetValue(source.FileName, out var nameMatches))
            {
                // 文件名相同的情况，只要大小相同就认为是相同文件
                matched = nameMatches.FirstOrDefault(candidate => AreSameByNameAndSize(source, candidate));
                if (matched != null)
                {
                    matchedTargets.Add(matched.FullPath);
                    results.Add(CreateItem(source, matched, SyncStatus.UpToDate, options));
                    continue;
                }

                var differentNameMatch = nameMatches.First();
                matchedTargets.Add(differentNameMatch.FullPath);
                results.Add(CreateItem(source, differentNameMatch, SyncStatus.PendingCopy, options));
                continue;
            }

            matched = FindMetadataMatch(source, targetByBucket);
            if (matched != null)
            {
                matchedTargets.Add(matched.FullPath);
                results.Add(CreateItem(source, matched, SyncStatus.UpToDate, options));
                continue;
            }

            results.Add(CreateItem(source, null, SyncStatus.PendingCopy, options));
        }

        foreach (var target in targetFiles)
        {
            if (matchedTargets.Contains(target.FullPath))
            {
                continue;
            }

            if (options.StartUtc.HasValue && target.LastWriteTimeUtc < options.StartUtc.Value)
            {
                continue;
            }

            if (options.EndUtc.HasValue && target.LastWriteTimeUtc > options.EndUtc.Value)
            {
                continue;
            }

            results.Add(new FileSyncItem
            {
                FileName = target.FileName,
                SourcePath = null,
                TargetPath = target.FullPath,
                TargetSize = target.Size,
                TargetModified = target.LastWriteTimeLocal,
                PlannedTargetPath = target.FullPath,
                Status = SyncStatus.TargetOnly
            });
        }

        return results.OrderBy(r => r.Status).ThenBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<long, List<FileMetadata>> BuildSizeBuckets(IEnumerable<FileMetadata> files)
    {
        var buckets = new Dictionary<long, List<FileMetadata>>();
        foreach (var file in files)
        {
            var bucketKey = GetBucketKey(file.Size);
            if (!buckets.TryGetValue(bucketKey, out var list))
            {
                list = new List<FileMetadata>();
                buckets[bucketKey] = list;
            }

            list.Add(file);
        }

        return buckets;
    }

    private static long GetBucketKey(long size) => size / (SizeToleranceBytes + 1);

    private static FileSyncItem CreateItem(FileMetadata source, FileMetadata? target, SyncStatus status, SyncOptions options)
    {
        var planned = target?.FullPath ?? Path.Combine(options.TargetFolder, source.RelativePath);
        return new FileSyncItem
        {
            FileName = source.FileName,
            SourcePath = source.FullPath,
            SourceSize = source.Size,
            SourceModified = source.LastWriteTimeLocal,
            TargetPath = target?.FullPath,
            TargetSize = target?.Size,
            TargetModified = target?.LastWriteTimeLocal,
            PlannedTargetPath = planned,
            Status = status
        };
    }

    private static FileMetadata? FindMetadataMatch(FileMetadata source, Dictionary<long, List<FileMetadata>> buckets)
    {
        var bucketKey = GetBucketKey(source.Size);
        foreach (var key in new[] { bucketKey - 1, bucketKey, bucketKey + 1 })
        {
            if (!buckets.TryGetValue(key, out var candidates))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (AreSame(source, candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool AreSameByNameAndSize(FileMetadata left, FileMetadata right)
    {
        // 文件名相同的情况下，只要大小相同就视为相同文件
        var sizeDiff = Math.Abs(left.Size - right.Size);
        return sizeDiff <= SizeToleranceBytes;
    }

    private static bool AreSame(FileMetadata left, FileMetadata right)
    {
        // 文件名不同的情况下，需要大小相同 + (创建时间或修改时间有一个相同)
        var sizeDiff = Math.Abs(left.Size - right.Size);
        if (sizeDiff > SizeToleranceBytes)
        {
            return false;
        }

        var creationTimeDiff = Math.Abs((left.CreationTimeUtc - right.CreationTimeUtc).TotalSeconds);
        var modifiedTimeDiff = Math.Abs((left.LastWriteTimeUtc - right.LastWriteTimeUtc).TotalSeconds);

        return creationTimeDiff <= TimeToleranceSeconds || modifiedTimeDiff <= TimeToleranceSeconds;
    }

    private static List<FileMetadata> EnumerateFiles(
        string root,
        HashSet<string>? extensions,
        DateTime? startUtc,
        DateTime? endUtc,
        Action<string> log,
        string roleLabel)
    {
        var results = new List<FileMetadata>();
        foreach (var file in SafeEnumerateFiles(root, log, roleLabel))
        {
            if (extensions != null)
            {
                var extension = Path.GetExtension(file);
                if (string.IsNullOrEmpty(extension) || !extensions.Contains(extension))
                {
                    continue;
                }

                // Video(MOV) 特殊逻辑：如果是 .mov 文件且存在同名 .heic 文件，则跳过
                if (extension.Equals(".mov", StringComparison.OrdinalIgnoreCase))
                {
                    var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file);
                    var directory = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(fileNameWithoutExtension))
                    {
                        var heicPath = Path.Combine(directory, fileNameWithoutExtension + ".heic");
                        if (File.Exists(heicPath))
                        {
                            continue; // 跳过这个 MOV 文件，因为存在同名的 HEIC 文件
                        }
                    }
                }
            }

            FileMetadata? metadata;
            try
            {
                var info = new FileInfo(file);
                metadata = new FileMetadata(
                    info.FullName,
                    info.Name,
                    info.Length,
                    info.CreationTimeUtc,
                    info.CreationTime,
                    info.LastWriteTimeUtc,
                    info.LastWriteTime,
                    Path.GetRelativePath(root, info.FullName));
            }
            catch (Exception ex)
            {
                log($"{roleLabel} 文件读取失败：{file}，{ex.Message}");
                continue;
            }

            if (startUtc.HasValue && metadata.LastWriteTimeUtc < startUtc.Value)
            {
                continue;
            }

            if (endUtc.HasValue && metadata.LastWriteTimeUtc > endUtc.Value)
            {
                continue;
            }

            results.Add(metadata);
        }

        return results;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, Action<string> log, string roleLabel)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<string> files = Array.Empty<string>();
            IEnumerable<string> directories = Array.Empty<string>();

            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception ex)
            {
                log($"{roleLabel} 访问失败：{current}，{ex.Message}");
            }

            foreach (var file in files)
            {
                yield return file;
            }

            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch (Exception ex)
            {
                log($"{roleLabel} 子目录访问失败：{current}，{ex.Message}");
            }

            foreach (var dir in directories)
            {
                stack.Push(dir);
            }
        }
    }

    private static string EnsureDestinationPath(string targetRoot, FileSyncItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.PlannedTargetPath))
        {
            return item.PlannedTargetPath!;
        }

        if (item.SourcePath == null)
        {
            throw new InvalidOperationException("缺少目标路径。");
        }

        var relative = Path.GetFileName(item.SourcePath);
        var combined = Path.Combine(targetRoot, relative);
        item.PlannedTargetPath = combined;
        return combined;
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (Dispatcher.CheckAccess())
        {
            AppendLogInternal(line);
        }
        else
        {
            Dispatcher.Invoke(() => AppendLogInternal(line));
        }
    }

    private void AppendLogInternal(string text)
    {
        // 添加到队列，维护500条限制
        _logQueue.Enqueue(text);
        if (_logQueue.Count > MaxLogLines)
        {
            _logQueue.Dequeue();

            // 重建日志显示
            _logBuilder.Clear();
            foreach (var line in _logQueue)
            {
                _logBuilder.AppendLine(line);
            }
        }
        else
        {
            _logBuilder.AppendLine(text);
        }

        LogText = _logBuilder.ToString();

        // 写入文件
        try
        {
            File.AppendAllText(LogFilePath, text + Environment.NewLine);
        }
        catch
        {
            // 忽略文件写入错误，避免影响主流程
        }
    }

    private static string NormalizePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path ?? string.Empty);
        return Path.GetFullPath(expanded.Trim().Trim('"'));
    }

    private static string ResolveDefaultTargetPath()
    {
        var expanded = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Pictures\iCloud Photos\Photos");
        return expanded;
    }

    private void InitializeLogging()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }

            // 加载今天的日志文件，最多显示500条
            if (File.Exists(LogFilePath))
            {
                var lines = File.ReadAllLines(LogFilePath);
                var startIndex = Math.Max(0, lines.Length - MaxLogLines);
                for (int i = startIndex; i < lines.Length; i++)
                {
                    _logQueue.Enqueue(lines[i]);
                    _logBuilder.AppendLine(lines[i]);
                }
                LogText = _logBuilder.ToString();
            }
        }
        catch (Exception ex)
        {
            // 无法记录到日志，因为日志系统还没初始化
            MessageBox.Show(this, $"初始化日志系统失败：{ex.Message}", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<UserSettings>(json);
                if (settings != null)
                {
                    if (!string.IsNullOrWhiteSpace(settings.SourceFolder))
                        _sourceFolder = settings.SourceFolder;
                    if (!string.IsNullOrWhiteSpace(settings.TargetFolder))
                        _targetFolder = settings.TargetFolder;
                    _startDate = settings.StartDate;
                    _endDate = settings.EndDate;
                    _includeJpg = settings.IncludeJpg;
                    _includeMp4 = settings.IncludeMp4;
                    _includeVideoMov = settings.IncludeVideoMov;

                    OnPropertyChanged(nameof(SourceFolder));
                    OnPropertyChanged(nameof(TargetFolder));
                    OnPropertyChanged(nameof(StartDate));
                    OnPropertyChanged(nameof(EndDate));
                    OnPropertyChanged(nameof(IncludeJpg));
                    OnPropertyChanged(nameof(IncludeMp4));
                    OnPropertyChanged(nameof(IncludeVideoMov));
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"加载设置失败：{ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new UserSettings
            {
                SourceFolder = SourceFolder,
                TargetFolder = TargetFolder,
                StartDate = StartDate,
                EndDate = EndDate,
                IncludeJpg = IncludeJpg,
                IncludeMp4 = IncludeMp4,
                IncludeVideoMov = IncludeVideoMov
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            AppendLog($"保存设置失败：{ex.Message}");
        }
    }

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum SyncStatus
{
    PendingCopy = 0,
    UpToDate = 1,
    TargetOnly = 2
}

public sealed class FileSyncItem : INotifyPropertyChanged
{
    private string _fileName = string.Empty;
    private string? _sourcePath;
    private long? _sourceSize;
    private DateTime? _sourceModified;
    private string? _targetPath;
    private long? _targetSize;
    private DateTime? _targetModified;
    private string? _plannedTargetPath;
    private SyncStatus _status;

    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    public string? SourcePath
    {
        get => _sourcePath;
        set => SetProperty(ref _sourcePath, value);
    }

    public long? SourceSize
    {
        get => _sourceSize;
        set
        {
            if (SetProperty(ref _sourceSize, value))
            {
                OnPropertyChanged(nameof(SourceSizeMB));
            }
        }
    }

    public string? SourceSizeMB => _sourceSize.HasValue ? $"{_sourceSize.Value / (1024.0 * 1024.0):F2}" : null;

    public DateTime? SourceModified
    {
        get => _sourceModified;
        set => SetProperty(ref _sourceModified, value);
    }

    public string? TargetPath
    {
        get => _targetPath;
        set
        {
            if (SetProperty(ref _targetPath, value))
            {
                OnPropertyChanged(nameof(TargetDisplayPath));
            }
        }
    }

    public long? TargetSize
    {
        get => _targetSize;
        set
        {
            if (SetProperty(ref _targetSize, value))
            {
                OnPropertyChanged(nameof(TargetSizeMB));
            }
        }
    }

    public string? TargetSizeMB => _targetSize.HasValue ? $"{_targetSize.Value / (1024.0 * 1024.0):F2}" : null;

    public DateTime? TargetModified
    {
        get => _targetModified;
        set => SetProperty(ref _targetModified, value);
    }

    public string? PlannedTargetPath
    {
        get => _plannedTargetPath;
        set
        {
            if (SetProperty(ref _plannedTargetPath, value))
            {
                OnPropertyChanged(nameof(TargetDisplayPath));
            }
        }
    }

    public string? TargetDisplayPath => TargetPath ?? PlannedTargetPath;

    public SyncStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public string StatusLabel => Status switch
    {
        SyncStatus.PendingCopy => "等待同步",
        SyncStatus.UpToDate => "已存在",
        SyncStatus.TargetOnly => "仅目标存在",
        _ => "未知"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed record FileMetadata(
    string FullPath,
    string FileName,
    long Size,
    DateTime CreationTimeUtc,
    DateTime CreationTimeLocal,
    DateTime LastWriteTimeUtc,
    DateTime LastWriteTimeLocal,
    string RelativePath);

internal sealed class SyncOptions
{
    public string SourceFolder { get; init; } = string.Empty;
    public string TargetFolder { get; init; } = string.Empty;
    public DateTime? StartUtc { get; init; }
    public DateTime? EndUtc { get; init; }
    public HashSet<string>? Extensions { get; init; }
}

internal sealed class UserSettings
{
    public string SourceFolder { get; set; } = string.Empty;
    public string TargetFolder { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IncludeJpg { get; set; } = true;
    public bool IncludeMp4 { get; set; } = true;
    public bool IncludeVideoMov { get; set; } = true;
}
