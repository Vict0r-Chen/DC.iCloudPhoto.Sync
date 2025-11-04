using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace DC.iCloudPhoto.Sync;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const long SizeToleranceBytes = 1024;
    private const double TimeToleranceSeconds = 1d;
    private const int DefaultPageSize = 50;

    private readonly List<FileSyncItem> _allPreviewItems = new();
    private readonly ObservableCollection<FileSyncItem> _pagedItems = new();
    private readonly StringBuilder _logBuilder = new();

    private string _sourceFolder = @"E:\Photos";
    private string _targetFolder = ResolveDefaultTargetPath();
    private DateTime? _startDate;
    private DateTime? _endDate;
    private bool _includeJpg = true;
    private bool _includeMp4 = true;
    private int _pageSize = DefaultPageSize;
    private int _currentPage = 1;
    private bool _isBusy;
    private string _logText = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FileSyncItem> PagedItems => _pagedItems;

    public IReadOnlyList<int> PageSizeOptions { get; } = new[] { 25, 50, 100, 200 };

    public string SourceFolder
    {
        get => _sourceFolder;
        set => SetProperty(ref _sourceFolder, value);
    }

    public string TargetFolder
    {
        get => _targetFolder;
        set => SetProperty(ref _targetFolder, value);
    }

    public DateTime? StartDate
    {
        get => _startDate;
        set => SetProperty(ref _startDate, value);
    }

    public DateTime? EndDate
    {
        get => _endDate;
        set => SetProperty(ref _endDate, value);
    }

    public bool IncludeJpg
    {
        get => _includeJpg;
        set
        {
            if (SetProperty(ref _includeJpg, value))
            {
                OnPropertyChanged(nameof(CanSync));
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

    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)_allPreviewItems.Count / PageSize));

    public string PageSummary => $"第 {CurrentPage} 页 / 共 {TotalPages} 页（{_allPreviewItems.Count} 条）";

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

    private async void OnPreviewClicked(object sender, RoutedEventArgs e)
    {
        await RunBusyTask(async () =>
        {
            var options = BuildOptions();
            if (options == null)
            {
                return;
            }

            AppendLog("开始扫描源文件夹和目标文件夹...");
            var previewItems = await Task.Run(() => BuildPreviewInternal(options, AppendLog));
            ApplyPreview(previewItems);
            AppendLog($"预览完成，共 {previewItems.Count} 条记录。");
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
        LogText = string.Empty;
    }

    private void ApplyPreview(IReadOnlyList<FileSyncItem> items)
    {
        _allPreviewItems.Clear();
        _allPreviewItems.AddRange(items);
        CurrentPage = 1;
        RefreshPagination();
        OnPropertyChanged(nameof(CanSync));
    }

    private void RefreshPagination()
    {
        _pagedItems.Clear();
        var desiredPage = _allPreviewItems.Count == 0
            ? 1
            : Math.Max(1, Math.Min(CurrentPage, TotalPages));
        CurrentPage = desiredPage;

        var skip = (CurrentPage - 1) * PageSize;
        foreach (var item in _allPreviewItems.Skip(skip).Take(PageSize))
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
                matched = nameMatches.FirstOrDefault(candidate => AreSame(source, candidate));
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

    private static bool AreSame(FileMetadata left, FileMetadata right)
    {
        var sizeDiff = Math.Abs(left.Size - right.Size);
        if (sizeDiff > SizeToleranceBytes)
        {
            return false;
        }

        var timeDiff = Math.Abs((left.LastWriteTimeUtc - right.LastWriteTimeUtc).TotalSeconds);
        return timeDiff <= TimeToleranceSeconds;
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
            }

            FileMetadata? metadata;
            try
            {
                var info = new FileInfo(file);
                metadata = new FileMetadata(
                    info.FullName,
                    info.Name,
                    info.Length,
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
        _logBuilder.AppendLine(text);
        LogText = _logBuilder.ToString();
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
        set => SetProperty(ref _sourceSize, value);
    }

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
        set => SetProperty(ref _targetSize, value);
    }

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
