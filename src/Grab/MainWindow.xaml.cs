using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Grab.Services;
using Microsoft.Win32;

namespace Grab;

public partial class MainWindow : Window
{
    enum SourceKind { Link, File }

    SourceKind _source = SourceKind.Link;
    VideoInfo _info;
    string _localPath = "";
    string _previewSource = "";
    string _lastFile = "";
    string _lastQuery = "";
    List<SearchResult> _lastResults;

    double _duration;
    double _trimStart;
    double _trimEnd;
    int _height = 99999;
    int _fetchToken;

    bool _ready;
    bool _busy;
    bool _mediaReady;
    bool _playing;
    bool _previewMuted;
    bool _proxyTried;
    bool _pulsing;
    bool _suppress;          // true while we set the text box from code
    bool _cookiesBroken;     // cookies failed once, so skip them from now on
    double? _progress;
    bool _shotBusy;
    Task _engineTask;
    readonly List<Segment> _parts = new List<Segment>();
    bool _full;
    bool _volSync;
    WindowState _prevState;
    WindowStyle _prevStyle;
    ResizeMode _prevResize;
    string _pendingUrl = "";
    string _pendingSpec = "";
    DateTime _jobStart;
    string _progBase = "";

    JobBase _job;
    CancellationTokenSource _proxyCts;

    readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
    readonly DispatcherTimer _elapsed = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (s, e) => Win.StyleTitleBar(this);
        Loaded += async (s, e) => await OnLoadedAsync();
        Closing += (s, e) =>
        {
            _job?.Cancel();
            _proxyCts?.Cancel();
            try { Player.Close(); } catch { }
            TempFiles.CleanPreviews();
        };

        TrimBar.SeekRequested += SeekTo;
        TrimBar.RangeChanged += (start, end) =>
        {
            _trimStart = start;
            _trimEnd = end;
            UpdateTrimFields();
            ClampPlayhead();
            UpdateState();
        };

        _tick.Tick += Tick;
        _tick.Start();
        _elapsed.Tick += (s, e) => UpdateElapsed();
        _debounce.Tick += async (s, e) =>
        {
            _debounce.Stop();
            await SubmitAsync(false);
        };

        ApplySettingsToUi();
        _ready = true;
        UpdateState();
    }

    // ------------------------------------------------------------------ startup

    async Task OnLoadedAsync()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        AboutText.Text = "Grab " + (version != null ? version.ToString(3) : "");

        var yt = Tools.Find("yt-dlp");
        var ff = Tools.Find("ffmpeg");
        var deno = Tools.Find("deno");
        var ver = yt != null ? await YtDlp.VersionAsync() : "";

        YtVersionText.Text = yt == null ? "yt-dlp not found" : "yt-dlp " + ver;
        FfmpegText.Text = (ff == null ? "ffmpeg not found" : "ffmpeg ready") + "   ·   " + (deno == null ? "deno not found" : "deno ready");

        if (yt == null)
            ShowMessage("yt-dlp not found. Put yt-dlp.exe in the bin folder next to Grab.exe.", true);
        else if (ff == null)
            ShowMessage("ffmpeg not found: merging, MP3 and trimming won't work. Put ffmpeg.exe and ffprobe.exe in the bin folder.", true);
        else if (deno == null)
            ShowMessage("deno.exe not found in the bin folder. YouTube needs it, so YouTube downloads may fail.", true);

        if (yt != null) _engineTask = AutoUpdateEngineAsync();
    }

    /// <summary>YouTube changes often, so yt-dlp checks for a newer version once a day (can be turned off).</summary>
    async Task AutoUpdateEngineAsync()
    {
        var cfg = SettingsStore.Current;
        if (!cfg.AutoUpdateEngine) return;
        var last = new DateTime(Math.Max(0, cfg.LastEngineCheck));
        if (cfg.LastEngineCheck > 0 && DateTime.Now - last < TimeSpan.FromHours(24)) return;

        await YtDlp.UpdateAsync();
        cfg.LastEngineCheck = DateTime.Now.Ticks;
        SettingsStore.Save();
        var ver = await YtDlp.VersionAsync();
        if (!string.IsNullOrEmpty(ver)) YtVersionText.Text = "yt-dlp " + ver;
    }

    /// <summary>Wait for a running engine update so a download never starts halfway through one.</summary>
    async Task WaitForEngineAsync()
    {
        var task = _engineTask;
        if (task != null && !task.IsCompleted)
        {
            SetHint("Updating the download engine…");
            try { await task; } catch { }
            SetHint("");
        }
    }

    void ApplySettingsToUi()
    {
        var c = SettingsStore.Current;
        if (c.Mode == "audio") RbAudio.IsChecked = true; else RbVideo.IsChecked = true;
        foreach (var rb in new[] { RbMp4, RbMkv, RbWebm }) rb.IsChecked = (string)rb.Tag == c.VideoFormat;
        foreach (var rb in new[] { RbMp3, RbM4a, RbOpus, RbWav }) rb.IsChecked = (string)rb.Tag == c.AudioFormat;
        foreach (var rb in new[] { Rb128, Rb192, Rb320 }) rb.IsChecked = (string)rb.Tag == c.AudioBitrate.ToString();
        foreach (var rb in new[] { RbCkNone, RbCkChrome, RbCkEdge, RbCkFirefox, RbCkBrave }) rb.IsChecked = (string)rb.Tag == c.Cookies;
        DirText.Text = c.Dir;
        RefreshCookiesFileText();
        AutoPreviewSwitch.IsChecked = c.AutoPreview;
        AutoUpdateSwitch.IsChecked = c.AutoUpdateEngine;
        RefreshTempText();
    }

    // ------------------------------------------------------------------ state

    bool IsAudio => RbAudio.IsChecked == true;
    bool TrimOn => TrimSwitch.IsChecked == true;

    void UpdateState()
    {
        if (!_ready) return;
        bool link = _source == SourceKind.Link;
        bool hasMedia = link ? _info != null : !string.IsNullOrEmpty(_localPath);

        ResSection.Visibility = link && !IsAudio ? Visibility.Visible : Visibility.Collapsed;
        VideoFormatSection.Visibility = IsAudio ? Visibility.Collapsed : Visibility.Visible;
        AudioFormatSection.Visibility = IsAudio ? Visibility.Visible : Visibility.Collapsed;
        BitrateSection.Visibility = IsAudio && SettingsStore.Current.AudioFormat == "mp3" ? Visibility.Visible : Visibility.Collapsed;
        FastRow.Visibility = !link && !IsAudio && hasMedia ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = _lastResults != null && link && _info != null ? Visibility.Visible : Visibility.Collapsed;

        TrimSwitch.IsEnabled = _duration > 0;
        var partsNote = TrimOn && _parts.Count >= 2 ? " (" + _parts.Count + " parts)" : "";
        BtnGo.Content = (link ? (IsAudio ? "Download audio" : "Download video") : "Export") + partsNote;
        BtnGo.IsEnabled = hasMedia && !_busy;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        BtnGo.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Collapsed;
        OptionsScroll.IsEnabled = !busy;
        Omnibox.IsEnabled = !busy;
        if (busy)
        {
            _jobStart = DateTime.Now;
            _progBase = "";
            _elapsed.Start();
            _progress = null;
            StopPulse();
            ProgFill.Width = 0;
            ProgText.Text = "Starting…";
            ProgInfo.Text = "";
        }
        if (!busy) _elapsed.Stop();
        UpdateState();
    }

    void ShowMessage(string text, bool isError)
    {
        ErrorText.Text = text;
        ErrorText.Foreground = (Brush)FindResource(isError ? "DangerBrush" : "MutedBrush");
        ErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void ClearError() => ShowMessage("", false);

    void SetPreviewStatus(string text)
    {
        PreviewStatus.Text = text;
        PreviewStatusBox.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void SetHint(string text)
    {
        OmniHint.Text = text;
        OmniHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void SetUrlText(string text)
    {
        _suppress = true;
        UrlBox.Text = text;
        UrlBox.CaretIndex = text.Length;
        _suppress = false;
    }

    string CookieSpec => _cookiesBroken ? "" : SettingsStore.CookieSpec();

    void ResetPreview()
    {
        _fetchToken++;
        _proxyCts?.Cancel();
        _proxyCts = null;

        StopPlayback();
        try { Player.Source = null; } catch { }
        try { Player.Close(); } catch { }
        TempFiles.CleanPreviews();
        BtnLoadPlayer.Visibility = Visibility.Collapsed;

        _info = null;
        _localPath = "";
        _previewSource = "";
        _mediaReady = false;
        _proxyTried = false;
        _duration = 0;
        _trimStart = 0;
        _trimEnd = 0;
        _parts.Clear();

        ThumbImage.Source = null;
        ThumbImage.Visibility = Visibility.Collapsed;
        Player.Visibility = Visibility.Collapsed;
        ResultsScroll.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        PlayOverlay.Visibility = Visibility.Collapsed;
        SetPreviewStatus("");
        InfoPanel.Visibility = Visibility.Collapsed;
        TimelineCard.Visibility = Visibility.Collapsed;

        TrimSwitch.IsChecked = false;
        TrimFields.Visibility = Visibility.Collapsed;
        TrimBar.RangeEnabled = false;
        TrimBar.Duration = 0;
        TrimBar.Position = 0;
        TrimBar.Start = 0;
        TrimBar.End = 0;
        TrimBar.SetParts(null);
        PartsBox.Visibility = Visibility.Collapsed;
        TimeText.Text = "0:00 / 0:00";

        RbVideo.IsEnabled = true;
        BuildResChips(null);

        DonePanel.Visibility = Visibility.Collapsed;
        if (!_busy) BtnGo.Visibility = Visibility.Visible;
    }

    static bool IsUrl(string text)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
    }

    static string CleanInput(string text)
    {
        return (text ?? "").Trim().Trim('"');
    }

    // ------------------------------------------------------------------ the one box

    async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0) { LoadLocal(files[0]); return; }
            }
            if (!Clipboard.ContainsText()) return;
            var text = CleanInput(Clipboard.GetText());
            if (text.Length == 0) return;

            SetUrlText(text);
            UrlBox.Focus();
            if (IsUrl(text) || File.Exists(text))
            {
                _lastResults = null;
                await SubmitAsync(false);
            }
            else
            {
                SetHint("Press Enter to search");
            }
        }
        catch
        {
            ShowMessage("Couldn't read the clipboard. Press Ctrl+V in the box instead.", true);
        }
    }

    void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _suppress || _busy) return;
        _debounce.Stop();
        var text = CleanInput(UrlBox.Text);

        if (text.Length == 0)
        {
            _lastResults = null;
            SetHint("");
            ClearError();
            ResetPreview();
            UpdateState();
        }
        else if (IsUrl(text) || File.Exists(text))
        {
            SetHint("");
            _debounce.Start();                 // links and file paths load by themselves
        }
        else
        {
            SetHint("Press Enter to search");  // plain words wait for Enter
        }
    }

    async void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _debounce.Stop();
        await SubmitAsync(true);
    }

    /// <summary>Decides what the box holds: a link, a file path, or words to search for.</summary>
    async Task SubmitAsync(bool allowSearch)
    {
        if (_busy) return;
        var text = CleanInput(UrlBox.Text);
        if (text.Length == 0) return;

        if (IsUrl(text))
        {
            _lastResults = null;
            await FetchAsync();
        }
        else if (File.Exists(text))
        {
            LoadLocal(text);
        }
        else if (allowSearch && text.Length >= 2)
        {
            await SearchAsync(text);
        }
    }

    // ------------------------------------------------------------------ search

    async Task SearchAsync(string query)
    {
        await WaitForEngineAsync();
        ClearError();
        ResetPreview();
        var token = _fetchToken;
        _lastQuery = query;
        SetHint("Searching…");

        var r = await YtDlp.SearchAsync(query);
        if (token != _fetchToken) return;
        SetHint("");

        if (r.Items == null)
        {
            ShowMessage(r.Error, true);
            UpdateState();
            return;
        }
        if (r.Items.Count == 0)
        {
            ShowMessage("No results for \"" + query + "\".", false);
            return;
        }

        _lastResults = r.Items;
        ShowResults(r.Items);
        UpdateState();
    }

    void ShowResults(List<SearchResult> items)
    {
        ResetPreview();
        EmptyState.Visibility = Visibility.Collapsed;
        ResultsList.Children.Clear();
        foreach (var item in items) ResultsList.Children.Add(MakeResultRow(item));
        ResultsScroll.ScrollToTop();
        ResultsScroll.Visibility = Visibility.Visible;
    }

    Button MakeResultRow(SearchResult r)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var thumb = new Border
        {
            Width = 128,
            Height = 72,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("Panel2Brush"),
            Clip = new RectangleGeometry(new Rect(0, 0, 128, 72), 8, 8),
        };
        try
        {
            thumb.Child = new Image { Source = new BitmapImage(new Uri(r.Thumb)), Stretch = Stretch.UniformToFill };
        }
        catch
        {
            // thumbnail is optional
        }
        grid.Children.Add(thumb);

        var text = new StackPanel { Margin = new Thickness(14, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = r.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        var meta = string.Join("   ·   ", new[] { r.Channel, r.Duration > 0 ? Fmt.Time(r.Duration) : "" }.Where(x => !string.IsNullOrEmpty(x)));
        text.Children.Add(new TextBlock
        {
            Text = meta,
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var button = new Button
        {
            Content = grid,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.Click += async (s, e) => await PickResultAsync(r);
        return button;
    }

    async Task PickResultAsync(SearchResult r)
    {
        SetUrlText(r.Url);
        await FetchAsync();
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResults == null) return;
        SetUrlText(_lastQuery);
        ClearError();
        ShowResults(_lastResults);
        UpdateState();
    }

    // ------------------------------------------------------------------ link mode

    async Task FetchAsync()
    {
        if (_busy) return;
        var url = CleanInput(UrlBox.Text);
        if (!IsUrl(url)) return;

        await WaitForEngineAsync();
        ClearError();
        ResetPreview();
        _source = SourceKind.Link;
        var token = _fetchToken;
        SetHint("Loading…");
        var spec = CookieSpec;

        var result = await YtDlp.GetInfoAsync(url, spec);
        if (token != _fetchToken) return;
        SetHint("");

        if (result.Info == null)
        {
            ShowMessage(result.Error, true);
            UpdateState();
            return;
        }

        if (result.Info.CookiesSkipped)
        {
            _cookiesBroken = true;
            spec = "";
            ShowMessage("Your browser cookies couldn't be read, so Grab is continuing without them.", false);
        }
        else if (result.Info.CompatMode)
        {
            ShowMessage("YouTube blocked the normal way, so Grab is using a compatibility mode. Some resolutions may be missing.", false);
        }

        _info = result.Info;
        _duration = _info.Duration;
        TitleText.Text = _info.Title;
        MetaText.Text = string.Join("   ·   ", new[] { _info.Channel, _duration > 0 ? Fmt.Time(_duration) : "" }.Where(x => !string.IsNullOrEmpty(x)));
        InfoPanel.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        TimelineCard.Visibility = Visibility.Visible;
        TrimBar.Duration = _duration;
        UpdateTimeLabel(0);

        if (!string.IsNullOrEmpty(_info.Thumb))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(_info.Thumb);
                bmp.EndInit();
                ThumbImage.Source = bmp;
                ThumbImage.Visibility = Visibility.Visible;
            }
            catch
            {
                // thumbnail is optional
            }
        }

        BuildResChips(_info.Resolutions);
        if (_info.Resolutions.Count == 0)
        {
            RbAudio.IsChecked = true;
            RbVideo.IsEnabled = false;
        }
        UpdateState();

        _pendingUrl = url;
        _pendingSpec = spec;
        var auto = SettingsStore.Current.AutoPreview && (_duration <= 0 || _duration <= 7200);
        if (auto)
        {
            await StartPreviewAsync(token);
        }
        else
        {
            BtnLoadPlayer.Visibility = Visibility.Visible;
            SetPreviewStatus(SettingsStore.Current.AutoPreview
                ? "This video is long, so the player isn't loaded automatically."
                : "The player is off. Load it to watch and trim visually.");
        }
    }

    async Task StartPreviewAsync(int token)
    {
        BtnLoadPlayer.Visibility = Visibility.Collapsed;

        // the player copy needs room: skip it politely instead of filling the disk
        var needed = (_duration > 0 ? _duration * 120000.0 : 400000000.0) + 300000000.0;
        var free = DiskSpace.FreeBytes(AppPaths.TempDir);
        if (free >= 0 && free < needed)
        {
            SetPreviewStatus("Not enough free disk space for the player (about " + Fmt.Size(needed) + " needed, " + Fmt.Size(free) + " free). You can still trim by typing the times.");
            BtnLoadPlayer.Visibility = Visibility.Visible;
            return;
        }

        SetPreviewStatus("Preparing the player…");
        _proxyCts = new CancellationTokenSource();
        var cts = _proxyCts;
        var file = await PreviewMaker.MakeAsync(_pendingUrl, _pendingSpec, fraction =>
        {
            if (token == _fetchToken)
                SetPreviewStatus(fraction.HasValue ? "Preparing the player…  " + (int)(fraction.Value * 100) + "%" : "Preparing the player…");
        }, cts.Token);
        if (token != _fetchToken || cts.IsCancellationRequested) return;
        if (string.IsNullOrEmpty(file))
        {
            var left = DiskSpace.FreeBytes(AppPaths.TempDir);
            SetPreviewStatus(left >= 0 && left < 300000000.0
                ? "Your disk is almost full, so the player couldn't be loaded."
                : "Couldn't load the player. You can still type the start and end times.");
            BtnLoadPlayer.Visibility = Visibility.Visible;
            return;
        }
        LoadPreview(file);
    }

    async void LoadPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingUrl)) return;
        await StartPreviewAsync(_fetchToken);
    }

    void BuildResChips(List<ResOption> list)
    {
        ResPanel.Children.Clear();
        _height = 99999;
        if (list == null || list.Count == 0)
        {
            ResEmpty.Visibility = Visibility.Visible;
            return;
        }
        ResEmpty.Visibility = Visibility.Collapsed;

        var first = true;
        foreach (var o in list)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = o.Label,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = o.Size > 0 ? "~" + Fmt.Size(o.Size) : " ",
                FontSize = 11,
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            var rb = new RadioButton
            {
                GroupName = "res",
                Style = (Style)FindResource("ChipButton"),
                Content = content,
                Tag = o.Height,
                IsChecked = first,
            };
            rb.Click += (s, e) => { _height = (int)((RadioButton)s).Tag; RefreshChipColors(); };
            ResPanel.Children.Add(rb);
            if (first) _height = o.Height;
            first = false;
        }
        RefreshChipColors();
    }

    /// <summary>The selected chip is light, so its texts turn dark.</summary>
    void RefreshChipColors()
    {
        var dark = (Brush)FindResource("DarkBrush");
        var darkSoft = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        foreach (var child in ResPanel.Children)
        {
            if (child is RadioButton rb && rb.Content is StackPanel sp && sp.Children.Count > 1
                && sp.Children[0] is TextBlock label && sp.Children[1] is TextBlock sub)
            {
                var on = rb.IsChecked == true;
                label.Foreground = on ? dark : (Brush)FindResource("TextBrush");
                sub.Foreground = on ? darkSoft : (Brush)FindResource("MutedBrush");
            }
        }
    }

    // ------------------------------------------------------------------ file mode

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a video or audio file",
            Filter = "Video and audio|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.flv;*.m4v;*.ts;*.mpg;*.mpeg;*.3gp;*.mp3;*.m4a;*.wav;*.flac;*.ogg;*.opus|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) LoadLocal(dlg.FileName);
    }

    void LoadLocal(string path)
    {
        if (_busy) return;
        if (!File.Exists(path))
        {
            ShowMessage("That file doesn't exist.", true);
            return;
        }

        ResetPreview();
        ClearError();
        SetHint("");
        _lastResults = null;
        _source = SourceKind.File;
        _localPath = path;
        SetUrlText(path);

        TitleText.Text = Path.GetFileNameWithoutExtension(path);
        MetaText.Text = Fmt.Size(new FileInfo(path).Length) + "   ·   " + Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        InfoPanel.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        TimelineCard.Visibility = Visibility.Visible;
        SetPreviewStatus("Loading video…");
        LoadPreview(path);
        UpdateState();
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0) LoadLocal(files[0]);
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = CleanInput((string)e.Data.GetData(DataFormats.UnicodeText));
                if (IsUrl(text))
                {
                    SetUrlText(text);
                    _lastResults = null;
                    await FetchAsync();
                }
            }
        }
        catch
        {
            ShowMessage("Couldn't open what you dropped.", true);
        }
    }

    // ------------------------------------------------------------------ player

    void LoadPreview(string source)
    {
        _previewSource = source;
        _mediaReady = false;
        try
        {
            Player.IsMuted = true;                 // no blip of sound while waiting for the first frame
            Player.Source = new Uri(source);
            Player.Visibility = Visibility.Visible;
            Player.Play();
        }
        catch
        {
            SetPreviewStatus("Couldn't open the video.");
        }
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        Player.Position = TimeSpan.Zero;
        Player.IsMuted = _previewMuted;
        Player.Volume = Math.Min(1.0, VolSlider.Value / 100.0);

        if (Player.NaturalDuration.HasTimeSpan)
        {
            var d = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (_duration <= 0 && d > 0)
            {
                _duration = d;
                TrimBar.Duration = d;
            }
        }

        _mediaReady = true;
        _playing = false;
        ThumbImage.Visibility = Visibility.Collapsed;
        SetPreviewStatus("");
        PlayOverlay.Visibility = Visibility.Visible;
        BtnPlay.Content = "\uE768";
        UpdateTimeLabel(0);
        UpdateState();
    }

    async void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        _playing = false;

        // Windows can't play every format (MKV, WebM, some codecs): make a small MP4 copy and play that.
        if (!_proxyTried && !string.IsNullOrEmpty(_previewSource))
        {
            _proxyTried = true;
            var token = _fetchToken;
            _proxyCts = new CancellationTokenSource();
            var cts = _proxyCts;
            SetPreviewStatus("Preparing the player…");
            var proxy = await LocalExporter.MakeProxyAsync(_previewSource, cts.Token);
            if (cts.IsCancellationRequested || token != _fetchToken) return;
            if (!string.IsNullOrEmpty(proxy))
            {
                LoadPreview(proxy);
                return;
            }
        }

        Player.Visibility = Visibility.Collapsed;
        if (ThumbImage.Source != null) ThumbImage.Visibility = Visibility.Visible;
        SetPreviewStatus("Can't play this one here. You can still type the start and end times.");
    }

    void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        PausePlayback();
    }

    void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        if (_mediaReady) TogglePlay();
    }

    void PreviewBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PreviewBox.Clip = new RectangleGeometry(new Rect(0, 0, PreviewBox.ActualWidth, PreviewBox.ActualHeight), 16, 16);
    }

    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (!_mediaReady) return;
        if (_playing) PausePlayback(); else StartPlayback();
    }

    void StartPlayback()
    {
        var pos = Player.Position.TotalSeconds;
        if (TrimOn && _trimEnd > 0)
        {
            if (pos < _trimStart || pos >= _trimEnd - 0.05) Player.Position = TimeSpan.FromSeconds(_trimStart);
        }
        else if (_duration > 0 && pos >= _duration - 0.1)
        {
            Player.Position = TimeSpan.Zero;
        }
        Player.Play();
        _playing = true;
        BtnPlay.Content = "\uE769";
        PlayOverlay.Visibility = Visibility.Collapsed;
    }

    void PausePlayback()
    {
        try { Player.Pause(); } catch { }
        _playing = false;
        BtnPlay.Content = "\uE768";
        PlayOverlay.Visibility = _mediaReady ? Visibility.Visible : Visibility.Collapsed;
    }

    void StopPlayback()
    {
        try { Player.Stop(); } catch { }
        _playing = false;
        BtnPlay.Content = "\uE768";
    }

    void PreviewMute_Click(object sender, RoutedEventArgs e)
    {
        _previewMuted = !_previewMuted;
        Player.IsMuted = _previewMuted;
        BtnPreviewMute.Content = _previewMuted ? "\uE74F" : "\uE767";
    }

    double CurrentPosition => _mediaReady ? Player.Position.TotalSeconds : TrimBar.Position;

    void SeekTo(double seconds)
    {
        seconds = Math.Max(0, _duration > 0 ? Math.Min(seconds, _duration) : seconds);
        if (TrimOn && _trimEnd > 0) seconds = Math.Min(Math.Max(seconds, _trimStart), _trimEnd);
        if (_mediaReady) Player.Position = TimeSpan.FromSeconds(seconds);
        TrimBar.Position = seconds;
        UpdateTimeLabel(seconds);
    }

    /// <summary>Moves the playhead back inside the trim range if it ended up outside.</summary>
    void ClampPlayhead()
    {
        if (!TrimOn || _trimEnd <= 0) return;
        var pos = CurrentPosition;
        var clamped = Math.Min(Math.Max(pos, _trimStart), _trimEnd);
        if (Math.Abs(clamped - pos) > 0.05) SeekTo(clamped);
    }

    void Tick(object sender, EventArgs e)
    {
        if (!_mediaReady) return;
        var pos = Player.Position.TotalSeconds;
        if (TrimOn && _trimEnd > 0)
        {
            if (_playing && pos >= _trimEnd)
            {
                PausePlayback();
                Player.Position = TimeSpan.FromSeconds(_trimEnd);
                pos = _trimEnd;
            }
            else if (pos < _trimStart - 0.25 || pos > _trimEnd + 0.25)
            {
                pos = Math.Min(Math.Max(pos, _trimStart), _trimEnd);
                Player.Position = TimeSpan.FromSeconds(pos);
            }
        }
        TrimBar.Position = pos;
        UpdateTimeLabel(pos);
    }

    void UpdateTimeLabel(double pos)
    {
        TimeText.Text = Fmt.Time(pos) + " / " + Fmt.Time(_duration);
    }

    // ------------------------------------------------------------------ trim

    void TrimSwitch_Click(object sender, RoutedEventArgs e)
    {
        var on = TrimOn;
        if (on && _trimEnd <= 0)
        {
            _trimStart = 0;
            _trimEnd = _duration;
        }
        TrimBar.RangeEnabled = on;
        TrimFields.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateTrimFields();
        RefreshParts();
        ClampPlayhead();
        UpdateState();
    }

    void UpdateTrimFields()
    {
        TrimBar.Start = _trimStart;
        TrimBar.End = _trimEnd;
        StartBox.Text = Fmt.TimeTenths(_trimStart);
        EndBox.Text = Fmt.TimeTenths(_trimEnd);
        LengthText.Text = Fmt.TimeTenths(Math.Max(0, _trimEnd - _trimStart));
    }

    void TrimBox_Commit(object sender, RoutedEventArgs e)
    {
        if (!_ready || _duration <= 0) return;
        var oldStart = _trimStart;
        var oldEnd = _trimEnd;

        if (Fmt.TryParseTime(StartBox.Text, out var s)) _trimStart = Math.Max(0, Math.Min(s, _duration));
        if (Fmt.TryParseTime(EndBox.Text, out var en)) _trimEnd = Math.Max(0, Math.Min(en, _duration));

        if (_trimEnd - _trimStart < 0.2)
        {
            _trimStart = oldStart;
            _trimEnd = oldEnd;
            ShowMessage("The end has to be after the start.", true);
        }
        else
        {
            ClearError();
        }
        UpdateTrimFields();
        ClampPlayhead();
        UpdateState();
    }

    void TrimBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        TrimBox_Commit(sender, e);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    void SetStart_Click(object sender, RoutedEventArgs e)
    {
        var pos = CurrentPosition;
        if (_trimEnd <= 0) _trimEnd = _duration;
        if (pos < _trimEnd - 0.2) _trimStart = pos;
        UpdateTrimFields();
        ClampPlayhead();
    }

    void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        var pos = CurrentPosition;
        if (pos > _trimStart + 0.2) _trimEnd = Math.Min(pos, _duration > 0 ? _duration : pos);
        UpdateTrimFields();
        ClampPlayhead();
    }

    // ------------------------------------------------------------------ several parts

    void AddPart_Click(object sender, RoutedEventArgs e)
    {
        if (!TrimOn || _duration <= 0) return;
        var end = _trimEnd > 0 ? _trimEnd : _duration;
        if (end - _trimStart < 0.2) { ShowMessage("Pick a range first.", true); return; }
        if (_parts.Count >= 20) { ShowMessage("That's the most parts Grab can join (20).", true); return; }
        ClearError();
        _parts.Add(new Segment { Start = _trimStart, End = end });
        RefreshParts();
        UpdateState();
    }

    void ClearParts_Click(object sender, RoutedEventArgs e)
    {
        _parts.Clear();
        RefreshParts();
        UpdateState();
    }

    void RefreshParts()
    {
        PartsPanel.Children.Clear();
        TrimBar.SetParts(TrimOn ? _parts : null);
        PartsBox.Visibility = TrimOn && _parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!TrimOn) return;

        for (var i = 0; i < _parts.Count; i++)
        {
            var part = _parts[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = (i + 1) + "   " + Fmt.TimeTenths(part.Start) + " – " + Fmt.TimeTenths(part.End),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
            var remove = new Button
            {
                Content = "\uE711",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 10,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Style = (Style)FindResource("GhostButton"),
                ToolTip = "Remove this part",
            };
            remove.Click += (s, e) =>
            {
                _parts.Remove(part);
                RefreshParts();
                UpdateState();
            };
            row.Children.Add(remove);

            PartsPanel.Children.Add(new Border
            {
                Child = row,
                Background = (Brush)FindResource("Panel2Brush"),
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 3, 4, 3),
                Margin = new Thickness(0, 0, 6, 6),
            });
        }
    }

    // ------------------------------------------------------------------ screenshot

    async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_shotBusy) return;
        var link = _source == SourceKind.Link;
        if (link ? _info == null : string.IsNullOrEmpty(_localPath)) return;

        _shotBusy = true;
        var second = CurrentPosition;
        try
        {
            ShowMessage("Saving screenshot…", false);
            string input;
            if (!link)
            {
                input = _localPath;
            }
            else
            {
                input = await YtDlp.GetVideoUrlAsync(CleanInput(UrlBox.Text), _height, CookieSpec);
                if (string.IsNullOrEmpty(input) && File.Exists(_previewSource)) input = _previewSource;   // lower quality fallback
            }
            if (string.IsNullOrEmpty(input))
            {
                ShowMessage("Couldn't get a frame from this video.", true);
                return;
            }

            var result = await Screenshots.CaptureAsync(input, second, SettingsStore.Current.Dir, TitleText.Text);
            if (!string.IsNullOrEmpty(result.Error))
            {
                ShowMessage(result.Error, true);
            }
            else
            {
                _lastFile = result.File;
                ShowMessage("Screenshot saved: " + Path.GetFileName(result.File), false);
            }
        }
        finally
        {
            _shotBusy = false;
        }
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { SettingsOverlay.Visibility = Visibility.Collapsed; e.Handled = true; }
            return;
        }
        if (_full && e.Key == Key.Escape)
        {
            SetFullscreen(false);
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
            case Key.S:
                Screenshot_Click(this, e);
                e.Handled = true;
                break;
            case Key.F:
                SetFullscreen(!_full);
                e.Handled = true;
                break;
            case Key.Space:
                TogglePlay();
                e.Handled = true;
                break;
            case Key.Left:
                if (_mediaReady) SeekTo(CurrentPosition - (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1));
                e.Handled = true;
                break;
            case Key.Right:
                if (_mediaReady) SeekTo(CurrentPosition + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1));
                e.Handled = true;
                break;
            case Key.OemOpenBrackets:
                if (TrimOn) SetStart_Click(this, e);
                e.Handled = true;
                break;
            case Key.OemCloseBrackets:
                if (TrimOn) SetEnd_Click(this, e);
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------ options

    void Mode_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.Mode = IsAudio ? "audio" : "video";
        SettingsStore.Save();
        UpdateState();
    }

    void VideoFormat_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.VideoFormat = (string)((RadioButton)sender).Tag;
        SettingsStore.Save();
    }

    void AudioFormat_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.AudioFormat = (string)((RadioButton)sender).Tag;
        SettingsStore.Save();
        UpdateState();
    }

    void Bitrate_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse((string)((RadioButton)sender).Tag, out var kbps)) SettingsStore.Current.AudioBitrate = kbps;
        SettingsStore.Save();
    }

    void ChangeDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to save", InitialDirectory = SettingsStore.Current.Dir };
        if (dlg.ShowDialog(this) == true)
        {
            SettingsStore.Current.Dir = dlg.FolderName;
            SettingsStore.Save();
            DirText.Text = dlg.FolderName;
        }
    }

    // ------------------------------------------------------------------ volume

    void VolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _volSync) return;
        var v = (int)Math.Round(e.NewValue);
        _volSync = true;
        VolBox.Text = v.ToString();
        _volSync = false;
        ApplyVolume(v);
    }

    void ApplyVolume(int percent)
    {
        try { Player.Volume = Math.Min(1.0, percent / 100.0); } catch { }
        VolHint.Text = percent == 0
            ? "The sound will be removed."
            : percent == 100
                ? "100% is the original volume. 0% removes the sound."
                : percent > 100
                    ? percent + "% is louder than the original. The player can only preview up to 100%."
                    : percent + "% is quieter than the original.";
    }

    void VolBox_Commit(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (!int.TryParse(VolBox.Text.Trim().TrimEnd('%'), out var v)) v = (int)Math.Round(VolSlider.Value);
        v = Math.Max(0, Math.Min(200, v));
        _volSync = true;
        VolSlider.Value = v;
        VolBox.Text = v.ToString();
        _volSync = false;
        ApplyVolume(v);
    }

    void VolBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        VolBox_Commit(sender, e);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    void VolReset_Click(object sender, RoutedEventArgs e)
    {
        VolSlider.Value = 100;
    }

    // ------------------------------------------------------------------ full screen

    void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_full);

    void Preview_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _mediaReady) SetFullscreen(!_full);
    }

    void SetFullscreen(bool on)
    {
        if (on == _full) return;
        if (on && !_mediaReady && _info == null && string.IsNullOrEmpty(_localPath)) return;
        _full = on;

        if (on)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            Topmost = true;
        }
        else
        {
            Topmost = false;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            WindowState = _prevState;
        }

        var show = on ? Visibility.Collapsed : Visibility.Visible;
        TopBar.Visibility = show;
        RightPanel.Visibility = show;
        ColSpacer.Width = new GridLength(on ? 0 : 16);
        ColRight.Width = new GridLength(on ? 0 : 360);
        MainGrid.Margin = on ? new Thickness(0) : new Thickness(20, 0, 20, 16);
        InfoPanel.Visibility = !on && (_info != null || !string.IsNullOrEmpty(_localPath)) ? Visibility.Visible : Visibility.Collapsed;
        BtnFullscreen.Content = on ? "\uE73F" : "\uE740";
        BtnFullscreen.ToolTip = on ? "Exit full screen (Esc)" : "Full screen (F)";
    }

    // ------------------------------------------------------------------ run

    async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await WaitForEngineAsync();
        ClearError();

        var cfg = SettingsStore.Current;
        var gain = VolSlider.Value / 100.0;

        // what to keep: several joined parts, one range, or everything
        var segments = new List<Segment>();
        var trim = false;
        double start = 0, end = 0;
        if (TrimOn && _duration > 0 && _parts.Count >= 2)
        {
            foreach (var part in _parts) segments.Add(new Segment { Start = part.Start, End = part.End });
        }
        else if (TrimOn && _duration > 0 && (_parts.Count == 1 || _trimEnd - _trimStart >= 0.2))
        {
            start = _parts.Count == 1 ? _parts[0].Start : _trimStart;
            end = _parts.Count == 1 ? _parts[0].End : _trimEnd;
            trim = true;
            if (end >= _duration - 0.05) end = 0;                 // until the very end
            if (start <= 0.05 && end == 0) trim = false;          // whole video, nothing to cut
        }

        var disk = CheckDisk(segments, trim, start, end);
        if (!string.IsNullOrEmpty(disk))
        {
            ShowMessage(disk, true);
            return;
        }

        SetBusy(true);
        JobResult result;
        try
        {
            if (_source == SourceKind.Link)
            {
                var job = new Downloader();
                job.Progress += OnProgress;
                _job = job;
                result = await job.RunAsync(new DownloadRequest
                {
                    Url = CleanInput(UrlBox.Text),
                    Audio = IsAudio,
                    Height = _height,
                    VideoFormat = cfg.VideoFormat,
                    AudioFormat = cfg.AudioFormat,
                    Bitrate = cfg.AudioBitrate,
                    Trim = trim,
                    Segments = segments,
                    TrimStart = start,
                    TrimEnd = end,
                    Gain = gain,
                    OutDir = cfg.Dir,
                    Cookies = CookieSpec,
                    Duration = _duration,
                });
            }
            else
            {
                var job = new LocalExporter();
                job.Progress += OnProgress;
                _job = job;
                result = await job.RunAsync(new LocalRequest
                {
                    Input = _localPath,
                    Audio = IsAudio,
                    VideoFormat = cfg.VideoFormat,
                    AudioFormat = cfg.AudioFormat,
                    Bitrate = cfg.AudioBitrate,
                    Trim = trim,
                    Segments = segments,
                    TrimStart = start,
                    TrimEnd = end,
                    Gain = gain,
                    FastCut = FastSwitch.IsChecked == true,
                    OutDir = cfg.Dir,
                    Duration = _duration,
                });
            }
        }
        catch (Exception ex)
        {
            result = new JobResult { Error = ex.Message };
        }

        _job = null;
        StopPulse();
        SetBusy(false);

        if (result.Ok)
        {
            _lastFile = result.File;
            DoneFile.Text = Path.GetFileName(result.File);
            BtnGo.Visibility = Visibility.Collapsed;
            DonePanel.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(result.Note))
            {
                _cookiesBroken = true;
                ShowMessage(result.Note, false);
            }
        }
        else if (!result.Cancelled)
        {
            ShowMessage(result.Error, true);
        }
    }

    /// <summary>Checks there is room for the result before anything is downloaded.</summary>
    string CheckDisk(List<Segment> segments, bool trim, double start, double end)
    {
        var cfg = SettingsStore.Current;
        const double margin = 300000000.0;
        double keepSeconds = _duration;
        if (segments.Count >= 2) keepSeconds = segments.Sum(x => x.Length);
        else if (trim) keepSeconds = (end > 0 ? end : _duration) - start;
        var ratio = _duration > 0 ? Math.Min(1.0, Math.Max(0.0, keepSeconds / _duration)) : 1.0;

        if (_source == SourceKind.Link)
        {
            double estimate;
            if (IsAudio)
            {
                estimate = _duration > 0 ? keepSeconds * 45000.0 : 0;      // about 360 kbps at most
            }
            else
            {
                var size = _info?.Resolutions.FirstOrDefault(x => x.Height == _height)?.Size ?? 0;
                estimate = size * ratio;
            }

            // the file is built in the temp folder first (video and audio side by side, then merged)...
            var tempProblem = DiskSpace.Check(AppPaths.TempDir, estimate * 2 + margin);
            if (!string.IsNullOrEmpty(tempProblem)) return tempProblem;

            // ...and then moved to the save folder
            if (!string.Equals(DiskSpace.DriveName(AppPaths.TempDir), DiskSpace.DriveName(cfg.Dir), StringComparison.OrdinalIgnoreCase))
                return DiskSpace.Check(cfg.Dir, estimate + margin);
            return "";
        }

        double fileSize = 0;
        try { fileSize = new FileInfo(_localPath).Length; } catch { }
        return DiskSpace.Check(cfg.Dir, fileSize * ratio * 1.5 + margin);
    }

    void OnProgress(ProgressInfo p)
    {
        _progress = p.Fraction;
        ProgText.Text = p.Fraction.HasValue ? p.Text + "  " + (int)(p.Fraction.Value * 100) + "%" : p.Text;
        _progBase = string.Join("   ·   ", new[] { p.Speed, p.Eta }.Where(x => !string.IsNullOrEmpty(x)));
        UpdateElapsed();
        ApplyProgress();
    }

    void UpdateElapsed()
    {
        if (!_busy) return;
        var spent = DateTime.Now - _jobStart;
        var elapsed = Fmt.Time(spent.TotalSeconds) + " elapsed";
        ProgInfo.Text = string.IsNullOrEmpty(_progBase) ? elapsed : _progBase + "   ·   " + elapsed;
    }

    void ApplyProgress()
    {
        var full = Math.Max(0, ProgTrack.ActualWidth - 2);
        if (_progress.HasValue)
        {
            StopPulse();
            ProgFill.Width = full * _progress.Value;
        }
        else
        {
            ProgFill.Width = full;
            StartPulse();
        }
    }

    void StartPulse()
    {
        if (_pulsing) return;
        _pulsing = true;
        var anim = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(800))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        ProgFill.BeginAnimation(OpacityProperty, anim);
    }

    void StopPulse()
    {
        if (!_pulsing) return;
        _pulsing = false;
        ProgFill.BeginAnimation(OpacityProperty, null);
        ProgFill.Opacity = 1;
    }

    void ProgTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_busy) ApplyProgress();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _job?.Cancel();
    }

    void Show_Click(object sender, RoutedEventArgs e) => Win.ShowInFolder(_lastFile);

    void Again_Click(object sender, RoutedEventArgs e)
    {
        DonePanel.Visibility = Visibility.Collapsed;
        BtnGo.Visibility = Visibility.Visible;
        if (_lastResults != null && _source == SourceKind.Link)
        {
            Back_Click(sender, e);                     // go back to the search results
            return;
        }
        SetUrlText("");
        _lastResults = null;
        ResetPreview();
        ClearError();
        SetHint("");
        UpdateState();
        UrlBox.Focus();
    }

    // ------------------------------------------------------------------ settings

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        RefreshTempText();
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    void SettingsClose_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    void SettingsBackdrop_Click(object sender, MouseButtonEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    void SettingsCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void AutoPreview_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.AutoPreview = AutoPreviewSwitch.IsChecked == true;
        SettingsStore.Save();
    }

    void AutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.AutoUpdateEngine = AutoUpdateSwitch.IsChecked == true;
        SettingsStore.Save();
    }

    void RefreshTempText()
    {
        var bytes = TempFiles.PreviewBytes();
        TempText.Text = bytes <= 0 ? "No temporary player files" : "Temporary player files: " + Fmt.Size(bytes);
    }

    void ClearTemp_Click(object sender, RoutedEventArgs e)
    {
        TempFiles.CleanPreviews();
        RefreshTempText();
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var download = AppLog.PathFor("last-download.log");
        var export = AppLog.PathFor("last-export.log");
        var newest = File.Exists(download) && (!File.Exists(export) || File.GetLastWriteTime(download) >= File.GetLastWriteTime(export))
            ? download : export;
        if (File.Exists(newest)) Win.Open(newest);
        else ShowMessage("There's no log yet. Run a download or export first.", false);
    }

    void Cookies_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.Cookies = (string)((RadioButton)sender).Tag ?? "";
        SettingsStore.Save();
        _cookiesBroken = false;
    }

    void RefreshCookiesFileText()
    {
        var file = SettingsStore.Current.CookiesFile;
        CookiesFileText.Text = string.IsNullOrEmpty(file) ? "No cookies file selected" : Path.GetFileName(file);
        BtnCookiesClear.Visibility = string.IsNullOrEmpty(file) ? Visibility.Collapsed : Visibility.Visible;
    }

    void CookiesFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a cookies.txt file",
            Filter = "Cookies file|*.txt|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        SettingsStore.Current.CookiesFile = dlg.FileName;
        SettingsStore.Save();
        _cookiesBroken = false;
        RefreshCookiesFileText();
    }

    void CookiesClear_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.CookiesFile = "";
        SettingsStore.Save();
        _cookiesBroken = false;
        RefreshCookiesFileText();
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        BtnUpdate.Content = "Updating…";
        var msg = await YtDlp.UpdateAsync();
        var ver = await YtDlp.VersionAsync();
        UpdateMsg.Text = msg;
        UpdateMsg.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(ver)) YtVersionText.Text = "yt-dlp " + ver;
        BtnUpdate.Content = "Update yt-dlp";
        BtnUpdate.IsEnabled = true;
    }

    void OpenAppFolder_Click(object sender, RoutedEventArgs e) => Win.Open(AppPaths.BaseDir);

    void GitHub_Click(object sender, RoutedEventArgs e) => Win.Open(AppInfo.RepoUrl);

    void Issues_Click(object sender, RoutedEventArgs e) => Win.Open(AppInfo.IssuesUrl);
}
