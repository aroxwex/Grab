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

    JobBase _job;
    CancellationTokenSource _proxyCts;

    readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
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
        };

        TrimBar.SeekRequested += SeekTo;
        TrimBar.RangeChanged += (start, end) =>
        {
            _trimStart = start;
            _trimEnd = end;
            UpdateTrimFields();
            UpdateState();
        };

        _tick.Tick += Tick;
        _tick.Start();
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
        var ver = yt != null ? await YtDlp.VersionAsync() : "";

        YtVersionText.Text = yt == null ? "yt-dlp not found" : "yt-dlp " + ver;
        FfmpegText.Text = ff == null ? "ffmpeg not found" : "ffmpeg ready";
        FooterLeft.Text = (yt == null ? "yt-dlp missing" : "yt-dlp " + ver) + "   ·   " + (ff == null ? "ffmpeg missing" : "ffmpeg ready");

        if (yt == null)
            ShowMessage("yt-dlp not found. Put yt-dlp.exe in the bin folder next to Grab.exe.", true);
        else if (ff == null)
            ShowMessage("ffmpeg not found: merging, MP3 and trimming won't work. Put ffmpeg.exe and ffprobe.exe in the bin folder.", true);
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
        MuteRow.Visibility = IsAudio ? Visibility.Collapsed : Visibility.Visible;
        FastRow.Visibility = !link && !IsAudio && hasMedia ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = _lastResults != null && link && _info != null ? Visibility.Visible : Visibility.Collapsed;

        TrimSwitch.IsEnabled = _duration > 0;
        BtnGo.Content = link ? (IsAudio ? "Download audio" : "Download video") : "Export";
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
            _progress = null;
            StopPulse();
            ProgFill.Width = 0;
            ProgText.Text = "Starting…";
            ProgInfo.Text = "";
        }
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

        _info = null;
        _localPath = "";
        _previewSource = "";
        _mediaReady = false;
        _proxyTried = false;
        _duration = 0;
        _trimStart = 0;
        _trimEnd = 0;

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

        SetPreviewStatus("Loading video…");
        var stream = await YtDlp.GetPreviewUrlAsync(url, spec);
        if (token != _fetchToken) return;
        if (string.IsNullOrEmpty(stream))
        {
            SetPreviewStatus("Can't play this one here. You can still type the start and end times.");
            return;
        }
        LoadPreview(stream);
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
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = o.Size > 0 ? "~" + Fmt.Size(o.Size) : " ",
                FontSize = 11,
                Opacity = 0.65,
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
            rb.Click += (s, e) => _height = (int)((RadioButton)s).Tag;
            ResPanel.Children.Add(rb);
            if (first) _height = o.Height;
            first = false;
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
        if (_mediaReady) Player.Position = TimeSpan.FromSeconds(seconds);
        TrimBar.Position = seconds;
        UpdateTimeLabel(seconds);
    }

    void Tick(object sender, EventArgs e)
    {
        if (!_mediaReady) return;
        var pos = Player.Position.TotalSeconds;
        if (_playing && TrimOn && _trimEnd > 0 && pos >= _trimEnd)
        {
            PausePlayback();
            Player.Position = TimeSpan.FromSeconds(_trimEnd);
            pos = _trimEnd;
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
    }

    void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        var pos = CurrentPosition;
        if (pos > _trimStart + 0.2) _trimEnd = Math.Min(pos, _duration > 0 ? _duration : pos);
        UpdateTrimFields();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { SettingsOverlay.Visibility = Visibility.Collapsed; e.Handled = true; }
            return;
        }
        if (Keyboard.FocusedElement is TextBox) return;

        switch (e.Key)
        {
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

    // ------------------------------------------------------------------ run

    async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ClearError();

        var cfg = SettingsStore.Current;
        var trim = TrimOn && _duration > 0 && _trimEnd - _trimStart >= 0.2;
        var start = trim ? _trimStart : 0;
        var end = trim ? _trimEnd : 0;
        if (trim && _duration > 0 && end >= _duration - 0.05) end = 0;       // until the very end
        if (trim && start <= 0.05 && end == 0) trim = false;                  // whole video, nothing to cut
        var mute = !IsAudio && MuteSwitch.IsChecked == true;

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
                    TrimStart = start,
                    TrimEnd = end,
                    Mute = mute,
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
                    TrimStart = start,
                    TrimEnd = end,
                    Mute = mute,
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

    void OnProgress(ProgressInfo p)
    {
        _progress = p.Fraction;
        ProgText.Text = p.Fraction.HasValue ? p.Text + "  " + (int)(p.Fraction.Value * 100) + "%" : p.Text;
        ProgInfo.Text = string.Join("   ·   ", new[] { p.Speed, p.Eta }.Where(x => !string.IsNullOrEmpty(x)));
        ApplyProgress();
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

    void Settings_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Visible;

    void SettingsClose_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    void SettingsBackdrop_Click(object sender, MouseButtonEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    void SettingsCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

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
}
