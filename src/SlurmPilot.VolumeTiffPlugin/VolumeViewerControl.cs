using Microsoft.Win32;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using SlurmPilot.Plugin.Abstractions;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SlurmPilot.VolumeTiffPlugin;

internal sealed class VolumeViewerControl : Grid, IDisposable
{
    private readonly IPluginContext _context;
    private readonly GLWpfControl _glControl = new();
    private readonly TextBlock _status = new();
    private readonly Button _openButton = new() { Content = "打开 TIFF…", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _openMp4Button = new() { Content = "打开高/低位 MP4…", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _infoButton = new() { Content = "查看信息", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _cancelButton = new() { Content = "取消读取", Padding = new Thickness(12, 6, 12, 6), IsEnabled = false };
    private readonly ProgressBar _loadProgress = new()
    {
        Minimum = 0,
        Maximum = 100,
        Height = 3,
        Visibility = Visibility.Collapsed
    };
    private readonly Button _autoButton = new() { Content = "Auto", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false };
    private readonly VolumeWindowRangeControl _windowRange = new() { IsEnabled = false };
    private readonly TextBlock _densityLabel = new() { Text = "密度 0.55", FontSize = 11 };
    private readonly TextBlock _thresholdLabel = new() { Text = "阈值 0.18", FontSize = 11 };
    private readonly TextBlock _zScaleLabel = new() { Text = "Z 比例 1.0×", FontSize = 11 };
    private readonly object _volumeGate = new();
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _infoCancellation;
    private VolumeData? _pendingVolume;
    private VolumeData? _volume;
    private bool _started;
    private bool _disposed;
    private bool _renderFailureReported;
    private bool _externalScissorReported;
    private int _program;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _volumeTexture;
    private int _sliceVertexCount;
    private int _sliceCount;
    private Vector3 _sliceDirection;
    private Matrix4 _rotation = Matrix4.Identity;
    private float _distance = 2.4f;
    private float _windowMinimum;
    private float _windowMaximum = 1f;
    private float _opacity = 0.55f;
    private float _threshold = 0.18f;
    private float _zScale = 1f;
    private int _displayMaximum = 65535;
    private int _imageMinimum;
    private int _imageMaximum = 65535;
    private int _autoMinimum;
    private int _autoMaximum = 65535;
    private bool _fitViewRequested;
    private Point _lastMouse;
    private bool _rotating;
    private string? _tiffPath;
    private (string High, string Low)? _mp4Pair;

    public VolumeViewerControl(IPluginContext context)
    {
        _context = context;
        SetResourceReference(BackgroundProperty, "BgBaseBrush");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        BuildToolbar();
        BuildViewport();
        Loaded += OnLoaded;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _infoCancellation?.Cancel();
        _infoCancellation?.Dispose();
        _infoCancellation = null;
        lock (_volumeGate) _pendingVolume = null;
        _volume = null;
        _glControl.Render -= Render;
        _glControl.MouseLeftButtonDown -= OnMouseDown;
        _glControl.MouseLeftButtonUp -= OnMouseUp;
        _glControl.MouseMove -= OnMouseMove;
        _glControl.MouseWheel -= OnMouseWheel;
        if (_glControl is IDisposable disposable) disposable.Dispose();
        Children.Clear();
        _context.Log(PluginLogLevel.Information, "Volume viewer disposed; CPU and OpenGL resources released.");
    }

    private void BuildToolbar()
    {
        var toolbar = new Grid { Margin = new Thickness(14, 12, 14, 10) };
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var commandRow = new Grid();
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        commandRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _openButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _openMp4Button.SetResourceReference(StyleProperty, "NeutralButtonStyle");
        _infoButton.SetResourceReference(StyleProperty, "NeutralButtonStyle");
        _cancelButton.SetResourceReference(StyleProperty, "GhostButtonStyle");
        _openButton.Click += OpenTiff;
        _openMp4Button.Click += OpenMp4Pair;
        _infoButton.Click += ShowSourceInfo;
        _cancelButton.Click += (_, _) => _loadCancellation?.Cancel();
        commandRow.Children.Add(_openButton);
        SetColumn(_openMp4Button, 1);
        _openMp4Button.Margin = new Thickness(8, 0, 0, 0);
        commandRow.Children.Add(_openMp4Button);
        SetColumn(_infoButton, 2);
        _infoButton.Margin = new Thickness(8, 0, 0, 0);
        commandRow.Children.Add(_infoButton);
        SetColumn(_cancelButton, 3);
        _cancelButton.Margin = new Thickness(4, 0, 0, 0);
        commandRow.Children.Add(_cancelButton);

        _status.Text = "请选择多页 TIFF 或一组 _high/_low MP4。左键拖动任意旋转，滚轮缩放。";
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextAlignment = TextAlignment.Right;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        SetColumn(_status, 4);
        commandRow.Children.Add(_status);
        toolbar.Children.Add(commandRow);

        var adjustmentRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        adjustmentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
        adjustmentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        adjustmentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        adjustmentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        adjustmentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        adjustmentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        adjustmentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        adjustmentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        adjustmentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _densityLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _thresholdLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _zScaleLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _windowRange.RangeChanged += (_, _) => UpdateWindowValues();
        var windowLabel = new TextBlock { Text = "显示范围", FontSize = 11 };
        windowLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var windowPanel = new StackPanel();
        windowPanel.Children.Add(windowLabel);
        windowPanel.Children.Add(_windowRange);
        adjustmentRow.Children.Add(windowPanel);

        _autoButton.SetResourceReference(StyleProperty, "NeutralButtonStyle");
        _autoButton.ToolTip = "忽略两端少量异常像素，自动设置显示最低值和最高值";
        _autoButton.VerticalAlignment = VerticalAlignment.Bottom;
        _autoButton.Margin = new Thickness(10, 0, 0, 0);
        _autoButton.Click += (_, _) => ApplyAutoWindow();
        SetColumn(_autoButton, 1);
        adjustmentRow.Children.Add(_autoButton);

        var densitySlider = new Slider { Minimum = 0, Maximum = 1, Value = _opacity };
        densitySlider.ValueChanged += (_, e) =>
        {
            _opacity = (float)e.NewValue;
            _densityLabel.Text = $"密度 {_opacity:0.00}";
            _glControl.InvalidateVisual();
        };
        var densityPanel = SliderPanel(_densityLabel, densitySlider);
        SetColumn(densityPanel, 3);
        adjustmentRow.Children.Add(densityPanel);

        var thresholdSlider = new Slider { Minimum = 0, Maximum = 0.65, Value = _threshold };
        thresholdSlider.ValueChanged += (_, e) =>
        {
            _threshold = (float)e.NewValue;
            _thresholdLabel.Text = $"阈值 {_threshold:0.00}";
            _glControl.InvalidateVisual();
        };
        var thresholdPanel = SliderPanel(_thresholdLabel, thresholdSlider);
        SetColumn(thresholdPanel, 5);
        adjustmentRow.Children.Add(thresholdPanel);

        var zScaleSlider = new Slider { Minimum = 0.2, Maximum = 5, Value = _zScale };
        zScaleSlider.ValueChanged += (_, e) =>
        {
            _zScale = (float)e.NewValue;
            _zScaleLabel.Text = $"Z 比例 {_zScale:0.0}×";
            _fitViewRequested = true;
            _glControl.InvalidateVisual();
        };
        var zScalePanel = SliderPanel(_zScaleLabel, zScaleSlider);
        SetColumn(zScalePanel, 7);
        adjustmentRow.Children.Add(zScalePanel);
        SetRow(adjustmentRow, 1);
        toolbar.Children.Add(adjustmentRow);

        _loadProgress.Margin = new Thickness(0, 8, 0, 0);
        SetRow(_loadProgress, 2);
        toolbar.Children.Add(_loadProgress);
        Children.Add(toolbar);
    }

    private static StackPanel SliderPanel(TextBlock label, Slider slider)
    {
        var panel = new StackPanel();
        panel.Children.Add(label);
        slider.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(slider);
        return panel;
    }

    private void BuildViewport()
    {
        var border = new Border { Margin = new Thickness(14, 0, 14, 14), CornerRadius = new CornerRadius(8), ClipToBounds = true };
        border.SetResourceReference(Border.BackgroundProperty, "BgCrustBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        border.BorderThickness = new Thickness(1);
        border.Child = _glControl;
        SetRow(border, 1);
        Children.Add(border);
        _glControl.Render += Render;
        _glControl.MouseLeftButtonDown += OnMouseDown;
        _glControl.MouseLeftButtonUp += OnMouseUp;
        _glControl.MouseMove += OnMouseMove;
        _glControl.MouseWheel += OnMouseWheel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || _disposed) return;
        try
        {
            _glControl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3 });
            _started = true;
            _context.Log(PluginLogLevel.Information, "OpenGL 3.3 context initialized.");
        }
        catch (Exception ex)
        {
            _status.Text = $"OpenGL 初始化失败：{ex.Message}";
            _context.Log(PluginLogLevel.Error, "OpenGL initialization failed.", ex);
        }
    }

    private async void OpenTiff(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "TIFF 图像|*.tif;*.tiff|所有文件|*.*", CheckFileExists = true };
        var accepted = Application.Current?.MainWindow is Window owner
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();
        if (accepted != true) return;
        _tiffPath = dialog.FileName;
        _mp4Pair = null;
        BeginLoad("正在读取并验证 TIFF 体数据…", indeterminate: false);
        var queuedForDisplay = false;
        try
        {
            var progress = new Progress<double>(value =>
            {
                _loadProgress.Value = value * 100;
                _status.Text = $"正在读取 TIFF 体数据… {value:P0}";
            });
            var data = await Task.Run(() => TiffVolumeReader.Read(
                dialog.FileName, _loadCancellation!.Token, progress));
            ConfigureWindowForVolume(data);
            lock (_volumeGate) _pendingVolume = data;
            queuedForDisplay = true;
            _loadProgress.Value = 94;
            _status.Text = "读取完成，正在上传三维体数据…";
            _context.Log(PluginLogLevel.Information,
                $"Loaded TIFF volume {data.Width}x{data.Height}x{data.Depth}, {data.BitsPerSample}-bit.");
            _glControl.InvalidateVisual();
        }
        catch (OperationCanceledException) { _status.Text = "读取已取消。"; }
        catch (Exception ex)
        {
            _status.Text = $"读取失败：{ex.Message}";
            _context.Log(PluginLogLevel.Error, "Failed to read TIFF volume.", ex);
            _context.ShowWarning("TIFF 读取失败", ex.Message, dialog.FileName);
        }
        finally { EndLoad(queuedForDisplay); }
    }

    private async void OpenMp4Pair(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "MP4 高/低位体数据|*.mp4|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = true,
            Title = "同时选择 _high.mp4 和 _low.mp4"
        };
        var accepted = Application.Current?.MainWindow is Window owner
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();
        if (accepted != true) return;
        if (!TryResolvePair(dialog.FileNames, out var high, out var low, out var error))
        {
            _context.ShowWarning("MP4 文件不匹配", error,
                "请同时选择同一组、文件名分别以 _high.mp4 和 _low.mp4 结尾的两个文件。");
            return;
        }
        _mp4Pair = (high, low);
        _tiffPath = null;

        BeginLoad("正在用 FFmpeg 解码并合并高/低 8 位 MP4…", indeterminate: true);
        var queuedForDisplay = false;
        try
        {
            var data = await FfmpegVolumeReader.ReadPairAsync(high, low, _context, _loadCancellation!.Token);
            ConfigureWindowForVolume(data);
            lock (_volumeGate) _pendingVolume = data;
            queuedForDisplay = true;
            _loadProgress.IsIndeterminate = false;
            _loadProgress.Value = 94;
            _status.Text = "解码完成，正在上传三维体数据…";
            _context.Log(PluginLogLevel.Information,
                $"Loaded high/low MP4 volume {data.Width}x{data.Height}x{data.Depth}, 16-bit.");
            _glControl.InvalidateVisual();
        }
        catch (OperationCanceledException) { _status.Text = "读取已取消。"; }
        catch (Exception ex)
        {
            _status.Text = $"MP4 读取失败：{ex.Message}";
            _context.Log(PluginLogLevel.Error, "Failed to read high/low MP4 volume.", ex);
            _context.ShowWarning("MP4 读取失败", ex.Message, $"高位：{high}\n低位：{low}");
        }
        finally { EndLoad(queuedForDisplay); }
    }

    private async void ShowSourceInfo(object sender, RoutedEventArgs e)
    {
        if (_tiffPath == null && _mp4Pair == null && !SelectSourceForInfo()) return;
        _infoCancellation?.Cancel();
        _infoCancellation?.Dispose();
        _infoCancellation = new CancellationTokenSource();
        _infoButton.IsEnabled = false;
        var oldStatus = _status.Text;
        _status.Text = "正在读取影像元数据…";
        try
        {
            if (_tiffPath != null)
            {
                var info = await Task.Run(() => TiffVolumeReader.Inspect(_tiffPath, _infoCancellation.Token));
                var ratio = info.XResolution is > 0 && info.YResolution is > 0
                    ? $"{info.YResolution / info.XResolution:0.####}:1"
                    : "未知";
                var details =
                    $"文件：{info.Path}\n" +
                    $"文件大小：{FormatBytes(info.FileSize)}\n" +
                    $"体数据尺寸：{info.Width} × {info.Height} × {info.Depth}\n" +
                    $"每页像素：{info.Width:N0} × {info.Height:N0}\n" +
                    $"位深：{info.BitsPerSample} bit\n" +
                    $"通道数：{info.SamplesPerPixel}\n" +
                    $"压缩：{info.Compression}\n" +
                    $"灰度解释：{info.Photometric}\n" +
                    $"存储：{(info.IsTiled ? "瓦片" : "扫描行/条带")}\n" +
                    $"X/Y 分辨率：{info.XResolution?.ToString("0.####") ?? "未知"} / {info.YResolution?.ToString("0.####") ?? "未知"} ({info.ResolutionUnit})\n" +
                    $"像素宽高比：{ratio}";
                _context.ShowInformation("TIFF 影像信息", Path.GetFileName(info.Path), details);
            }
            else if (_mp4Pair is { } pair)
            {
                var (highInfo, lowInfo) = await FfmpegVolumeReader.InspectPairAsync(
                    pair.High, pair.Low, _context, _infoCancellation.Token);
                var details = FfmpegVolumeReader.FormatInfo(highInfo, "高 8 位流") + "\n\n" +
                              FfmpegVolumeReader.FormatInfo(lowInfo, "低 8 位流") + "\n\n" +
                              $"合成规则：(high << 8) | low\n探测器：{highInfo.ProbeExecutable}";
                _context.ShowInformation("MP4 体数据信息",
                    $"{highInfo.Width} × {highInfo.Height} × {highInfo.FrameCount?.ToString() ?? "未知"} · 合成 16 位", details);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _context.ShowWarning("读取影像信息失败", ex.Message); }
        finally
        {
            _infoCancellation?.Dispose();
            _infoCancellation = null;
            _status.Text = oldStatus;
            _infoButton.IsEnabled = true;
        }
    }

    private void BeginLoad(string status, bool indeterminate)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        _openButton.IsEnabled = false;
        _openMp4Button.IsEnabled = false;
        _infoButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _status.Text = status;
        _loadProgress.IsIndeterminate = indeterminate;
        _loadProgress.Value = 0;
        _loadProgress.Visibility = Visibility.Visible;
    }

    private void EndLoad(bool awaitingUpload)
    {
        _openButton.IsEnabled = true;
        _openMp4Button.IsEnabled = true;
        _infoButton.IsEnabled = true;
        _cancelButton.IsEnabled = false;
        if (!awaitingUpload) HideLoadProgress();
    }

    private void HideLoadProgress()
    {
        _loadProgress.IsIndeterminate = false;
        _loadProgress.Value = 0;
        _loadProgress.Visibility = Visibility.Collapsed;
    }

    private void ConfigureWindowForVolume(VolumeData data)
    {
        _displayMaximum = data.BitsPerSample == 8 ? byte.MaxValue : ushort.MaxValue;
        _imageMinimum = ToDisplayValue(data.Minimum, data.BitsPerSample);
        _imageMaximum = ToDisplayValue(data.Maximum, data.BitsPerSample);
        (_autoMinimum, _autoMaximum) = CalculateAutoRange(data);
        _windowRange.IsEnabled = true;
        _autoButton.IsEnabled = true;
        _windowRange.Configure(_imageMinimum, _imageMaximum, _imageMinimum, _imageMaximum);
        _fitViewRequested = true;
    }

    private void ApplyAutoWindow()
    {
        if (!_windowRange.IsEnabled) return;
        ApplyWindow(_autoMinimum, _autoMaximum);
    }

    private void ApplyWindow(int minimum, int maximum)
    {
        if (maximum <= minimum)
        {
            minimum = 0;
            maximum = _displayMaximum;
        }
        _windowRange.SetValues(minimum, maximum);
    }

    internal static int ToDisplayValue(ushort value, int bitsPerSample)
        => bitsPerSample == 8 ? (int)Math.Round(value / 257d) : value;

    internal static long EstimateDisplayBytes(VolumeData data)
        => checked((long)data.Width * data.Height * data.Depth * sizeof(ushort));

    internal static float CalculateVoxelOpacity(float signal, float density)
    {
        if (signal <= 0.001f || density <= 0f) return 0f;
        var shapedSignal = MathF.Pow(Math.Clamp(signal, 0f, 1f), 1.25f);
        return Math.Clamp((0.06f + shapedSignal * 0.94f) * density, 0f, 0.92f);
    }

    internal static int CalculateRenderSliceCount(int fullSliceCount, bool interacting)
    {
        if (fullSliceCount <= 0) throw new ArgumentOutOfRangeException(nameof(fullSliceCount));
        return interacting ? Math.Clamp(fullSliceCount / 4, 48, 128) : fullSliceCount;
    }

    internal static float CalculateFitDistance(
        int width, int height, int depth, float viewportAspect, float verticalFieldOfViewDegrees = 42f)
        => CalculateFitDistance((float)width, height, depth, viewportAspect, verticalFieldOfViewDegrees);

    private static float CalculateFitDistance(
        float width, float height, float depth, float viewportAspect, float verticalFieldOfViewDegrees = 42f)
    {
        if (width <= 0 || height <= 0 || depth <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        viewportAspect = Math.Max(0.05f, viewportAspect);
        var largest = Math.Max(width, Math.Max(height, depth));
        var scaledWidth = width / largest;
        var scaledHeight = height / largest;
        var scaledDepth = depth / largest;
        var boundingRadius = 0.5f * MathF.Sqrt(
            scaledWidth * scaledWidth + scaledHeight * scaledHeight + scaledDepth * scaledDepth);
        var verticalHalfFov = MathHelper.DegreesToRadians(verticalFieldOfViewDegrees) * 0.5f;
        var horizontalHalfFov = MathF.Atan(MathF.Tan(verticalHalfFov) * viewportAspect);
        var limitingHalfFov = MathF.Min(verticalHalfFov, horizontalHalfFov);
        return boundingRadius / MathF.Sin(limitingHalfFov) * 1.12f;
    }

    private void UpdateWindowValues()
    {
        _windowMinimum = (float)(_windowRange.LowerValue / _displayMaximum);
        _windowMaximum = (float)(_windowRange.UpperValue / _displayMaximum);
        _glControl.InvalidateVisual();
    }

    internal static (int Minimum, int Maximum) CalculateAutoRange(VolumeData data)
    {
        const int bins16 = 4096;
        var binCount = data.BitsPerSample == 8 ? 256 : bins16;
        var histogram = new long[binCount];
        foreach (var voxel in data.Voxels)
        {
            var bin = data.BitsPerSample == 8 ? voxel >> 8 : voxel >> 4;
            histogram[Math.Min(bin, binCount - 1)]++;
        }

        // Ignore a small number of extreme pixels at each end, similar to an automatic
        // brightness/contrast stretch, so isolated hot/dead pixels do not flatten the volume.
        var clippedPerTail = Math.Max(1L, (long)(data.Voxels.LongLength * 0.0035));
        long cumulative = 0;
        var lowerBin = 0;
        for (; lowerBin < histogram.Length - 1; lowerBin++)
        {
            cumulative += histogram[lowerBin];
            if (cumulative > clippedPerTail) break;
        }
        cumulative = 0;
        var upperBin = histogram.Length - 1;
        for (; upperBin > lowerBin; upperBin--)
        {
            cumulative += histogram[upperBin];
            if (cumulative > clippedPerTail) break;
        }

        var minimum = data.BitsPerSample == 8 ? lowerBin : lowerBin << 4;
        var maximum = data.BitsPerSample == 8 ? upperBin : Math.Min(ushort.MaxValue, ((upperBin + 1) << 4) - 1);
        if (maximum <= minimum)
            return (0, data.BitsPerSample == 8 ? byte.MaxValue : ushort.MaxValue);
        return (minimum, maximum);
    }

    private bool SelectSourceForInfo()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "三维影像|*.tif;*.tiff;*.mp4|TIFF 图像|*.tif;*.tiff|MP4 视频|*.mp4|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = true,
            Title = "选择一个 TIFF，或同时选择 _high.mp4 与 _low.mp4"
        };
        var accepted = Application.Current?.MainWindow is Window owner
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();
        if (accepted != true) return false;
        if (dialog.FileNames.Length == 1 &&
            new[] { ".tif", ".tiff" }.Contains(Path.GetExtension(dialog.FileName), StringComparer.OrdinalIgnoreCase))
        {
            _tiffPath = dialog.FileName;
            _mp4Pair = null;
            return true;
        }
        if (TryResolvePair(dialog.FileNames, out var high, out var low, out var error))
        {
            _mp4Pair = (high, low);
            _tiffPath = null;
            return true;
        }
        _context.ShowWarning("无法识别影像文件", error,
            "查看 TIFF 信息时选择一个 .tif/.tiff；查看 MP4 信息时同时选择匹配的 _high.mp4 和 _low.mp4。");
        return false;
    }

    private static bool TryResolvePair(string[] paths, out string high, out string low, out string error)
    {
        high = paths.SingleOrDefault(path => Path.GetFileNameWithoutExtension(path)
            .EndsWith("_high", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        low = paths.SingleOrDefault(path => Path.GetFileNameWithoutExtension(path)
            .EndsWith("_low", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        if (paths.Length != 2 || high.Length == 0 || low.Length == 0)
        {
            error = "必须选择且只选择一份 _high.mp4 和一份 _low.mp4。";
            return false;
        }
        var highBase = Path.GetFileNameWithoutExtension(high)[..^5];
        var lowBase = Path.GetFileNameWithoutExtension(low)[..^4];
        if (!string.Equals(highBase, lowBase, StringComparison.OrdinalIgnoreCase))
        {
            error = $"两份 MP4 不属于同一组：{highBase} / {lowBase}";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]} ({bytes:N0} 字节)";
    }

    private void Render(TimeSpan delta)
    {
        if (_disposed || _renderFailureReported) return;
        try
        {
            RenderCore();
        }
        catch (Exception ex)
        {
            _renderFailureReported = true;
            HideLoadProgress();
            _status.Text = $"三维渲染失败：{ex.Message}";
            _context.Log(PluginLogLevel.Error, "Volume rendering failed.", ex);
        }
    }

    private void RenderCore()
    {
        EnsureRenderer();
        VolumeData? pending;
        lock (_volumeGate) { pending = _pendingVolume; _pendingVolume = null; }
        if (pending != null) UploadVolume(pending);

        var dpi = VisualTreeHelper.GetDpi(_glControl);
        var width = Math.Max(1, (int)Math.Ceiling(_glControl.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(_glControl.ActualHeight * dpi.DpiScaleY));
        if (_fitViewRequested && _volume != null)
        {
            _rotation = Matrix4.CreateRotationX(-0.35f) * Matrix4.CreateRotationY(0.65f);
            _distance = CalculateFitDistance(_volume.Width, _volume.Height, _volume.Depth * _zScale, width / (float)height);
            _fitViewRequested = false;
        }
        if (GL.IsEnabled(EnableCap.ScissorTest) && !_externalScissorReported)
        {
            _externalScissorReported = true;
            _context.Log(PluginLogLevel.Warning,
                "OpenGL scissor state leaked into the volume renderer; forcing a full-frame reset.");
        }
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.StencilTest);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.ColorMask(true, true, true, true);
        GL.DepthMask(true);
        GL.Viewport(0, 0, width, height);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (_volume == null || _volumeTexture == 0) return;

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.UseProgram(_program);
        GL.BindVertexArray(_vertexArray);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _volumeTexture);
        GL.Uniform1(GL.GetUniformLocation(_program, "uVolume"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "uWindowMinimum"), _windowMinimum);
        GL.Uniform1(GL.GetUniformLocation(_program, "uWindowMaximum"), _windowMaximum);
        GL.Uniform1(GL.GetUniformLocation(_program, "uOpacity"), _opacity);
        GL.Uniform1(GL.GetUniformLocation(_program, "uThreshold"), _threshold);

        var scaledDepth = _volume.Depth * _zScale;
        var largest = Math.Max(_volume.Width, Math.Max(_volume.Height, scaledDepth));
        var model = Matrix4.CreateScale(_volume.Width / largest, _volume.Height / largest, scaledDepth / largest)
                    * _rotation;
        var camera = new Vector3(0f, 0f, _distance);
        var view = Matrix4.LookAt(camera, Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(42f), width / (float)height, 0.05f, 100f);
        var mvp = model * view * projection;
        Matrix4.Invert(model, out var inverseModel);
        var cameraObject = Vector3.TransformPosition(camera, inverseModel);
        var viewDirection = Vector3.Normalize(cameraObject);
        var fullSliceCount = Math.Clamp((int)MathF.Ceiling(
            MathF.Abs(viewDirection.X) * _volume.Width +
            MathF.Abs(viewDirection.Y) * _volume.Height +
            MathF.Abs(viewDirection.Z) * _volume.Depth), 128, 768);
        var sliceCount = CalculateRenderSliceCount(fullSliceCount, _rotating);
        EnsureSliceGeometry(viewDirection, sliceCount);
        GL.Uniform1(GL.GetUniformLocation(_program, "uSampleScale"), fullSliceCount / (float)sliceCount);
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uMvp"), true, ref mvp);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _sliceVertexCount);
        GL.Disable(EnableCap.Blend);
        GL.BindTexture(TextureTarget.Texture3D, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private void EnsureRenderer()
    {
        if (_program != 0) return;
        _program = CreateProgram(VolumeVertexShader, VolumeFragmentShader);
        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
        GL.BindVertexArray(0);
    }

    private void UploadVolume(VolumeData data)
    {
        if (_volumeTexture != 0) GL.DeleteTexture(_volumeTexture);
        _volumeTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _volumeTexture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16, data.Width, data.Height, data.Depth,
            0, OpenTK.Graphics.OpenGL4.PixelFormat.Red, PixelType.UnsignedShort, data.Voxels);
        var uploadError = GL.GetError();
        if (uploadError != ErrorCode.NoError)
            throw new InvalidOperationException($"上传三维纹理失败（OpenGL {uploadError}）。");
        GL.BindTexture(TextureTarget.Texture3D, 0);
        _sliceDirection = Vector3.Zero;
        _sliceCount = 0;
        _volume = data with { Voxels = [] };
        _loadProgress.Value = 100;
        HideLoadProgress();
        var sourceName = _tiffPath != null
            ? Path.GetFileName(_tiffPath)
            : Path.GetFileNameWithoutExtension(_mp4Pair?.High ?? "体数据")
                .Replace("_high", string.Empty, StringComparison.OrdinalIgnoreCase);
        _status.Text = $"{sourceName} · {data.Width}×{data.Height}×{data.Depth} · " +
                       $"{data.BitsPerSample} 位 · 范围 {_imageMinimum}–{_imageMaximum} · " +
                       $"显示缓存约 {FormatBytes(EstimateDisplayBytes(data))}";
    }

    private void EnsureSliceGeometry(Vector3 viewDirection, int sliceCount)
    {
        if (_sliceCount == sliceCount && _sliceDirection.LengthSquared > 0f &&
            Vector3.Dot(_sliceDirection, viewDirection) > 0.99999f) return;
        var vertices = BuildViewAlignedSliceGeometry(viewDirection, sliceCount);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
        _sliceDirection = viewDirection;
        _sliceCount = sliceCount;
        _sliceVertexCount = vertices.Length / 6;
    }

    internal static float[] BuildViewAlignedSliceGeometry(Vector3 viewDirection, int sliceCount)
    {
        if (sliceCount <= 0) throw new ArgumentOutOfRangeException(nameof(sliceCount));
        if (viewDirection.LengthSquared <= 0.000001f) throw new ArgumentOutOfRangeException(nameof(viewDirection));
        var normal = Vector3.Normalize(viewDirection);
        var minimum = VolumeCorners.Min(point => Vector3.Dot(point, normal));
        var maximum = VolumeCorners.Max(point => Vector3.Dot(point, normal));
        var reference = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var basisU = Vector3.Normalize(Vector3.Cross(reference, normal));
        var basisV = Vector3.Cross(normal, basisU);
        var result = new List<float>(sliceCount * 9 * 6);
        var intersections = new List<Vector3>(6);
        for (var index = 0; index < sliceCount; index++)
        {
            var distance = minimum + (maximum - minimum) * (index + 0.5f) / sliceCount;
            intersections.Clear();
            foreach (var (startIndex, endIndex) in VolumeEdges)
            {
                var start = VolumeCorners[startIndex];
                var end = VolumeCorners[endIndex];
                var startDistance = Vector3.Dot(start, normal);
                var endDistance = Vector3.Dot(end, normal);
                var denominator = endDistance - startDistance;
                if (MathF.Abs(denominator) < 0.000001f) continue;
                var amount = (distance - startDistance) / denominator;
                if (amount < -0.00001f || amount > 1.00001f) continue;
                var point = Vector3.Lerp(start, end, Math.Clamp(amount, 0f, 1f));
                if (intersections.All(existing => (existing - point).LengthSquared > 0.0000001f))
                    intersections.Add(point);
            }
            if (intersections.Count < 3) continue;
            var center = Vector3.Zero;
            foreach (var point in intersections) center += point;
            center /= intersections.Count;
            intersections.Sort((left, right) =>
            {
                var leftOffset = left - center;
                var rightOffset = right - center;
                var leftAngle = MathF.Atan2(Vector3.Dot(leftOffset, basisV), Vector3.Dot(leftOffset, basisU));
                var rightAngle = MathF.Atan2(Vector3.Dot(rightOffset, basisV), Vector3.Dot(rightOffset, basisU));
                return leftAngle.CompareTo(rightAngle);
            });
            for (var triangle = 1; triangle < intersections.Count - 1; triangle++)
            {
                AddVolumeVertex(result, intersections[0]);
                AddVolumeVertex(result, intersections[triangle]);
                AddVolumeVertex(result, intersections[triangle + 1]);
            }
        }
        return result.ToArray();
    }

    private static void AddVolumeVertex(List<float> target, Vector3 position)
    {
        target.Add(position.X);
        target.Add(position.Y);
        target.Add(position.Z);
        target.Add(position.X + 0.5f);
        target.Add(position.Y + 0.5f);
        target.Add(position.Z + 0.5f);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _rotating = true;
        _lastMouse = e.GetPosition(_glControl);
        _glControl.CaptureMouse();
    }
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rotating) return;
        _rotating = false;
        _sliceDirection = Vector3.Zero;
        _glControl.ReleaseMouseCapture();
        _glControl.InvalidateVisual();
    }
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_rotating) return;
        var point = e.GetPosition(_glControl);
        var deltaX = (float)(point.X - _lastMouse.X);
        var deltaY = (float)(point.Y - _lastMouse.Y);
        if (MathF.Abs(deltaX) > 0.001f || MathF.Abs(deltaY) > 0.001f)
            _rotation = ApplyDragRotation(_rotation, deltaX, deltaY);
        _lastMouse = point;
        _glControl.InvalidateVisual();
    }

    internal static Matrix4 ApplyDragRotation(Matrix4 current, float deltaX, float deltaY)
    {
        const float radiansPerPixel = 0.008f;
        var rotated = current
                      * Matrix4.CreateRotationY(deltaX * radiansPerPixel)
                      * Matrix4.CreateRotationX(deltaY * radiansPerPixel);
        var orientation = rotated.ExtractRotation();
        orientation.Normalize();
        return Matrix4.CreateFromQuaternion(orientation);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.88f : 1.14f), 0.6f, 50f);
        _glControl.InvalidateVisual();
        e.Handled = true;
    }

    private static int CreateProgram(string vertexSource, string fragmentSource)
    {
        static int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }
        var vertex = Compile(ShaderType.VertexShader, vertexSource);
        var fragment = Compile(ShaderType.FragmentShader, fragmentSource);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var ok);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        if (ok == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        return program;
    }

    private static readonly Vector3[] VolumeCorners =
    [
        new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f),
        new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f),
        new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f),
        new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f)
    ];

    private static readonly (int Start, int End)[] VolumeEdges =
    [
        (0, 1), (1, 2), (2, 3), (3, 0),
        (4, 5), (5, 6), (6, 7), (7, 4),
        (0, 4), (1, 5), (2, 6), (3, 7)
    ];

    internal const string VolumeVertexShader = """
        #version 330 core
        layout(location=0) in vec3 aPosition;
        layout(location=1) in vec3 aTextureCoordinate;
        uniform mat4 uMvp;
        out vec3 vTextureCoordinate;
        void main() {
            vTextureCoordinate = aTextureCoordinate;
            gl_Position = vec4(aPosition, 1.0) * uMvp;
        }
        """;

    private const string VolumeFragmentShader = """
        #version 330 core
        in vec3 vTextureCoordinate;
        out vec4 fragColor;
        uniform sampler3D uVolume;
        uniform float uWindowMinimum;
        uniform float uWindowMaximum;
        uniform float uOpacity;
        uniform float uThreshold;
        uniform float uSampleScale;

        void main() {
            float value = texture(uVolume, vTextureCoordinate).r;
            float windowed = clamp((value - uWindowMinimum) /
                max(0.000015, uWindowMaximum - uWindowMinimum), 0.0, 1.0);
            float signal = clamp((windowed - uThreshold) /
                max(0.0001, 1.0 - uThreshold), 0.0, 1.0);
            if (signal <= 0.001) discard;
            signal = pow(signal, 1.25);
            float baseAlpha = clamp((0.06 + signal * 0.94) * uOpacity, 0.0, 0.92);
            float alpha = 1.0 - pow(1.0 - baseAlpha, uSampleScale);
            fragColor = vec4(vec3(signal), alpha);
        }
        """;
}
