using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPFS.Services;

namespace IPFS.ViewModels;

/// <summary>当前预览区展示的内容类型</summary>
public enum FilePreviewKind
{
    None,
    Video,
    Audio,
    Image,
    Text
}

/// <summary>
/// 详情页视图模型：专辑信息、文件列表、图片/文本预览与内置播放控制。
/// </summary>
public partial class DetailViewModel(PageContext context, Album album, IPlaybackService playback) : PageViewModelBase(context)
{
    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".ts", ".webm", ".m4v", ".mpg", ".mpeg"
    };

    static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".ape"
    };

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };

    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".log", ".srt", ".ass"
    };

    CancellationTokenSource previewCts = new();

    public Album Album { get; } = album;

    public IPlaybackService Playback { get; } = playback;

    /// <summary>当前专辑的文件列表（来自数据库，必要时从 IPFS 刷新）</summary>
    public ObservableCollection<FileData> Files { get; } = [];

    [ObservableProperty]
    private FileData? selectedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? cover;

    [ObservableProperty]
    private string content = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoMode), nameof(IsAudioMode), nameof(IsPlaybackMode), nameof(IsImageMode), nameof(IsTextMode), nameof(IsPlaceholderMode), nameof(HasPlaybackHint))]
    private FilePreviewKind previewKind;

    [ObservableProperty]
    private Bitmap? previewImage;

    [ObservableProperty]
    private string previewText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaybackHint))]
    private string previewHint = "选择文件后可预览";

    /// <summary>播放器里是否已装载媒体：用于判断「播放/暂停」是加载新视频还是恢复播放</summary>
    bool hasMedia;

    ScrubController? scrub;
    /// <summary>进度条拖动仲裁：拖动期间冻结回写，松手后继续冻结直到 seek 生效</summary>
    public ScrubController Scrub => scrub ??= new ScrubController(
        getLength: () => Length,
        applyPosition: p => Position = p,
        seek: p => Playback.Position = (float)p);

    /// <summary>播放进度 0~1，与进度条同步</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double position;

    /// <summary>媒体总时长（毫秒）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private long length;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private int volume = 100;

    [ObservableProperty]
    private bool isMuted;

    /// <summary>影院模式：隐藏专辑信息与文件列表，让播放器占满页面</summary>
    [ObservableProperty]
    private bool isExtended;

    /// <summary>已播放时长文本，随进度实时变化</summary>
    public string TimeText => FormatTime((long)(Position * Length));

    /// <summary>总时长文本</summary>
    public string DurationText => FormatTime(Length);

    public string Title => Album.Name;

    public string Date => Album.Date;

    public bool IsVideoMode => PreviewKind == FilePreviewKind.Video;

    public bool IsAudioMode => PreviewKind == FilePreviewKind.Audio;

    /// <summary>视频或音频：控制条是否出现的判断依据</summary>
    public bool IsPlaybackMode => IsVideoMode || IsAudioMode;

    public bool IsImageMode => PreviewKind == FilePreviewKind.Image;

    public bool IsTextMode => PreviewKind == FilePreviewKind.Text;

    public bool IsPlaceholderMode => PreviewKind == FilePreviewKind.None;

    /// <summary>播放模式下是否需要在控制条上方显示提示（加载中、播放失败等）</summary>
    public bool HasPlaybackHint => IsPlaybackMode && !string.IsNullOrEmpty(PreviewHint);

    public bool HasCover => Cover != null;

    /// <summary>进入页面时加载专辑信息与文件列表</summary>
    public override async Task InitializeAsync()
    {
        //页面可能被重复初始化，先退订再订阅，避免同一处理器被挂多次
        Playback.StateChanged -= OnPlaybackStateChanged;
        Playback.StateChanged += OnPlaybackStateChanged;
        Playback.Failed -= OnPlaybackFailed;
        Playback.Failed += OnPlaybackFailed;
        Playback.Ended -= OnPlaybackEnded;
        Playback.Ended += OnPlaybackEnded;
        //播放器全局共享，进入页面时同步真实音量，避免界面与实际不一致
        Volume = Playback.Volume;
        IsMuted = Playback.Mute;

        IsBusy = true;
        Log.SetStatus($"加载「{Album.Name}」…");
        try
        {
            //数据库中的老专辑可能没有文件列表，此时从 IPFS 补取
            if (Album.Files.Count == 0)
                await Album.GetFolderInfoAsync();

            RebuildFiles();
            //默认选中第一个普通文件，避免一进页面就停在封面/简介上；只有特殊文件时退回第一项
            if (Files.Count > 0)
                SelectedFile = Files.FirstOrDefault(f => SpecialRank(f) == 2) ?? Files[0];

            await LoadAlbumInfoAsync(previewCts.Token);
            Log.SetStatus("就绪");
        }
        catch (OperationCanceledException)
        {
            //页面已关闭
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "加载详情失败");
            Log.Append($"加载详情失败：{ex.Message}");
            Log.SetStatus("加载详情失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    #region 命令

    [RelayCommand(CanExecute = nameof(CanPlaySelected))]
    void PlaySelected()
    {
        if (SelectedFile is not FileData file) return;
        hasMedia = true;
        //加载可能持续数秒，先给出提示，避免点了没反应
        PreviewKind = IsAudioFile(file) ? FilePreviewKind.Audio : FilePreviewKind.Video;
        PreviewHint = $"正在加载 {file.Name} …";
        Log.SetStatus($"加载 {file.Name} …");
        Playback.Play(file, s => Dispatcher.UIThread.Post(() => Log.SetStatus(s)));
    }

    /// <summary>播放/暂停：尚未加载媒体时等同于文件列表旁的「播放选中文件」</summary>
    [RelayCommand]
    void PauseOrResume()
    {
        if (hasMedia)
        {
            Playback.TogglePause();
            return;
        }
        if (CanPlaySelected()) PlaySelected();
    }

    [RelayCommand]
    void Stop()
    {
        hasMedia = false;
        Playback.Stop();
        Position = 0;
    }

    /// <summary>切换静音，音量值保持不变</summary>
    [RelayCommand]
    void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>切换影院模式：播放器占满页面并随窗体缩放</summary>
    [RelayCommand]
    void ToggleExtended() => IsExtended = !IsExtended;

    partial void OnVolumeChanged(int value) => Playback.Volume = value;

    partial void OnIsMutedChanged(bool value) => Playback.Mute = value;

    [RelayCommand]
    async Task OpenLocalAsync()
    {
        var path = await Dialogs.PickVideoFileAsync("打开视频文件");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            //本地视频也切换到视频模式，否则 VideoView 不可见、画面无处显示
            hasMedia = true;
            PreviewKind = FilePreviewKind.Video;
            PreviewHint = $"正在加载 {Path.GetFileName(path)} …";
            Playback.Play(path, s => Dispatcher.UIThread.Post(() => Log.SetStatus(s)));
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "打开本地文件失败");
            Log.Append(ex.Message);
        }
    }

    [RelayCommand]
    async Task RefreshFilesAsync()
    {
        IsBusy = true;
        Log.SetStatus("从 IPFS 加载文件列表…");
        try
        {
            //重建列表会清空选中项，先记住文件名，重建后恢复原来的选中文件
            var selectedName = SelectedFile?.Name;
            await Album.GetFolderInfoAsync();
            RebuildFiles();
            if (selectedName != null)
                SelectedFile = Files.FirstOrDefault(f => string.Equals(f.Name, selectedName, StringComparison.Ordinal));
            await LoadAlbumInfoAsync(previewCts.Token);
            Log.SetStatus("文件列表已刷新");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "刷新文件列表失败");
            Log.Append($"刷新文件列表失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>下载单个文件到所选目录</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    async Task DownloadFileAsync(FileData? file)
    {
        if (file == null) return;
        var path = await Dialogs.PickFolderAsync($"下载「{file.Name}」到…");
        if (string.IsNullOrEmpty(path)) return;

        IsBusy = true;
        try
        {
            Log.SetStatus($"下载 {file.Name} …");
            await IPFSHttpAPI.DownloadFileAsync(file, path);
            Log.Append($"文件「{file.Name}」下载完成：{Path.Combine(path, file.Name)}");
            Log.SetStatus("下载完成");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "下载文件失败");
            Log.Append($"下载失败：{ex.Message}");
            Log.SetStatus("下载失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    bool CanDownload(FileData? file) => file != null && !IsBusy;

    /// <summary>复制单个文件的 CID</summary>
    [RelayCommand]
    async Task CopyFileCidAsync(FileData? file)
    {
        if (file == null) return;
        await Dialogs.CopyToClipboardAsync(file.Cid);
        Log.SetStatus("已复制 CID");
    }

    #endregion

    bool CanPlaySelected() => SelectedFile != null && IsPlayableFile(SelectedFile);

    protected override void OnBusyChanged(bool isBusy) => DownloadFileCommand.NotifyCanExecuteChanged();

    partial void OnSelectedFileChanged(FileData? value)
    {
        PlaySelectedCommand.NotifyCanExecuteChanged();
        DownloadFileCommand.NotifyCanExecuteChanged();
        _ = PreviewAsync(value);
    }

    /// <summary>列表项双击时调用：视频/音频开始播放，其他类型重新预览</summary>
    public void ActivateSelected()
    {
        if (SelectedFile == null) return;
        if (IsPlayableFile(SelectedFile)) PlaySelectedCommand.Execute(null);
        else _ = PreviewAsync(SelectedFile);
    }

    /// <summary>离开页面时停止播放（由视图在脱离可视树时调用）</summary>
    public void StopPlayback()
    {
        hasMedia = false;
        Playback.Stop();
    }

    void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        if (Scrub.IsScrubbing) return;

        var playing = Playback.IsPlaying;
        //开始播放后收起「正在加载」提示
        if (playing && !IsPlaying) PreviewHint = string.Empty;
        IsPlaying = playing;
        Length = Playback.Length;

        if (Scrub.TryAcceptSample())
            //停止后 libvlc 的 Position 为 -1，界面上按 0 显示
            Position = Playback.Position < 0 ? 0 : Playback.Position;
    }

    void OnPlaybackFailed(string message) => Dispatcher.UIThread.Post(() =>
    {
        PreviewHint = $"播放失败：{message}";
        Log.Append($"播放失败：{message}");
        Log.SetStatus("播放失败");
    });

    /// <summary>播完后 libvlc 停在 Ended 状态，清掉已装载标记，下次按播放键重新加载选中视频</summary>
    void OnPlaybackEnded(object? sender, EventArgs e)
    {
        hasMedia = false;
        IsPlaying = false;
        Position = 0;
    }

    async Task LoadAlbumInfoAsync(CancellationToken token)
    {
        //简介与封面都来自 IPFS 网络，放到后台线程避免阻塞界面
        await Task.Run(() => Album.LoadInfoAsync(), token);
        Content = Album.Content;
        if (Album.Cover == null) return;

        var bitmap = await ImagePreviewService.ToWriteableBitmapAsync(Album.Cover, 480, 360, token);
        if (bitmap != null) Cover = bitmap;
    }

    void RebuildFiles()
    {
        Files.Clear();
        //封面、简介固定在最前；其余文件保持数据库中的原始顺序，避免打乱已有的目录块顺序
        List<FileData> covers = [], contents = [], others = [];
        foreach (var file in Album.Files)
        {
            switch (SpecialRank(file))
            {
                case 0: covers.Add(file); break;
                case 1: contents.Add(file); break;
                default: others.Add(file); break;
            }
        }
        foreach (var file in covers) Files.Add(file);
        foreach (var file in contents) Files.Add(file);
        foreach (var file in others) Files.Add(file);
    }

    /// <summary>置顶优先级：0=封面，1=简介，2=其他；只对专辑根目录的文件生效</summary>
    static int SpecialRank(FileData file)
    {
        var name = file.Name;
        if (name.Contains('\\') || name.Contains('/')) return 2;
        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.Equals(stem, "Cover", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(stem, "Content", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    async Task PreviewAsync(FileData? file)
    {
        previewCts.Cancel();
        previewCts.Dispose();
        previewCts = new CancellationTokenSource();
        var token = previewCts.Token;

        PreviewKind = FilePreviewKind.None;
        PreviewImage = null;
        PreviewText = string.Empty;
        PreviewHint = "选择文件后可预览";

        if (file == null) return;

        //切换到非媒体文件时停止正在播放的音视频，避免音频继续在后台播放
        if (!IsPlayableFile(file))
        {
            hasMedia = false;
            Playback.Stop();
        }

        var extension = Path.GetExtension(file.Name);
        if (VideoExtensions.Contains(extension))
        {
            PreviewKind = FilePreviewKind.Video;
            PreviewHint = "双击文件或点击文件列表上方的播放按钮开始播放";
            return;
        }

        if (AudioExtensions.Contains(extension))
        {
            PreviewKind = FilePreviewKind.Audio;
            PreviewHint = "双击文件或点击文件列表上方的播放按钮开始播放";
            return;
        }

        try
        {
            if (ImageExtensions.Contains(extension))
            {
                PreviewKind = FilePreviewKind.Image;
                PreviewHint = $"加载图片 {file.Name} …";
                Log.SetStatus($"加载图片 {file.Name} …");
                var bitmap = await ImagePreviewService.LoadAsync(file.Cid, 1280, 800, token);
                if (bitmap == null) return;
                if (token.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }
                PreviewImage = bitmap;
                PreviewHint = string.Empty;
                Log.SetStatus("就绪");
                return;
            }

            if (TextExtensions.Contains(extension))
            {
                PreviewKind = FilePreviewKind.Text;
                PreviewHint = $"加载文本 {file.Name} …";
                Log.SetStatus($"加载文本 {file.Name} …");
                PreviewText = await LoadTextAsync(file, token);
                PreviewHint = string.Empty;
                Log.SetStatus("就绪");
                return;
            }

            PreviewHint = "该文件类型暂不支持预览，可下载后查看";
        }
        catch (OperationCanceledException)
        {
            //已切换到其他文件
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "预览文件失败");
            PreviewHint = $"预览失败：{ex.Message}";
            Log.Append($"预览失败：{ex.Message}");
        }
    }

    static async Task<string> LoadTextAsync(FileData file, CancellationToken token)
    {
        using var stream = await IPFSHttpAPI.DownloadAsync(IPFSHttpAPI.BuildCommand("cat", file.Cid));
        using var reader = new StreamReader(stream);
        var buffer = new char[64 * 1024];
        int read = await reader.ReadAsync(buffer, token);
        return read == buffer.Length
            ? new string(buffer, 0, read) + Environment.NewLine + "…（内容过长，已截断）"
            : new string(buffer, 0, read);
    }

    static bool IsVideoFile(FileData file) => VideoExtensions.Contains(Path.GetExtension(file.Name));

    static bool IsAudioFile(FileData file) => AudioExtensions.Contains(Path.GetExtension(file.Name));

    static bool IsPlayableFile(FileData file) => IsVideoFile(file) || IsAudioFile(file);

    /// <summary>毫秒转 mm:ss，超过一小时补上小时段</summary>
    static string FormatTime(long milliseconds)
    {
        if (milliseconds <= 0) return "00:00";
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    partial void OnCoverChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    partial void OnPreviewImageChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    public override void Dispose()
    {
        if (disposed) return;
        base.Dispose();

        previewCts.Cancel();
        previewCts.Dispose();
        Playback.StateChanged -= OnPlaybackStateChanged;
        Playback.Failed -= OnPlaybackFailed;
        Playback.Ended -= OnPlaybackEnded;
        Playback.Stop();
        PreviewImage = null;
        Cover = null;
    }
}
