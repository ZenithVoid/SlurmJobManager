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
    private readonly object _volumeGate = new();
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _infoCancellation;
    private VolumeData? _pendingVolume;
    private VolumeData? _volume;
    private bool _started;
    private bool _disposed;
    private int _program;
    private int _vertexArray;
    private int _vertexBuffer;
    private int _volumeTexture;
    private float _yaw = 0.65f;
    private float _pitch = -0.35f;
    private float _distance = 2.4f;
    private float _threshold = 0.08f;
    private float _opacity = 0.75f;
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
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _openButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _openMp4Button.SetResourceReference(StyleProperty, "NeutralButtonStyle");
        _infoButton.SetResourceReference(StyleProperty, "NeutralButtonStyle");
        _cancelButton.SetResourceReference(StyleProperty, "GhostButtonStyle");
        _openButton.Click += OpenTiff;
        _openMp4Button.Click += OpenMp4Pair;
        _infoButton.Click += ShowSourceInfo;
        _cancelButton.Click += (_, _) => _loadCancellation?.Cancel();
        toolbar.Children.Add(_openButton);
        SetColumn(_openMp4Button, 1);
        _openMp4Button.Margin = new Thickness(8, 0, 0, 0);
        toolbar.Children.Add(_openMp4Button);
        SetColumn(_infoButton, 2);
        _infoButton.Margin = new Thickness(8, 0, 0, 0);
        toolbar.Children.Add(_infoButton);
        SetColumn(_cancelButton, 3);
        _cancelButton.Margin = new Thickness(4, 0, 0, 0);
        toolbar.Children.Add(_cancelButton);

        var thresholdPanel = SliderPanel("阈值", _threshold, value => _threshold = (float)value);
        SetColumn(thresholdPanel, 5);
        toolbar.Children.Add(thresholdPanel);
        var opacityPanel = SliderPanel("密度", _opacity, value => _opacity = (float)value);
        SetColumn(opacityPanel, 7);
        toolbar.Children.Add(opacityPanel);

        _status.Text = "请选择多页 TIFF 或一组 _high/_low MP4。左键拖动旋转，滚轮缩放。";
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextAlignment = TextAlignment.Right;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        SetColumn(_status, 8);
        toolbar.Children.Add(_status);
        Children.Add(toolbar);
    }

    private StackPanel SliderPanel(string label, double value, Action<double> changed)
    {
        var panel = new StackPanel();
        var text = new TextBlock { Text = label, FontSize = 11 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(text);
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, Margin = new Thickness(0, 2, 0, 0) };
        slider.ValueChanged += (_, e) => { changed(e.NewValue); _glControl.InvalidateVisual(); };
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
        BeginLoad("正在读取并验证 TIFF 体数据…");
        try
        {
            var data = await Task.Run(() => TiffVolumeReader.Read(dialog.FileName, _loadCancellation!.Token));
            lock (_volumeGate) _pendingVolume = data;
            _status.Text = $"{System.IO.Path.GetFileName(dialog.FileName)} · {data.Width}×{data.Height}×{data.Depth} · {data.BitsPerSample} 位";
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
        finally { EndLoad(); }
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

        BeginLoad("正在用 FFmpeg 解码并合并高/低 8 位 MP4…");
        try
        {
            var data = await FfmpegVolumeReader.ReadPairAsync(high, low, _context, _loadCancellation!.Token);
            lock (_volumeGate) _pendingVolume = data;
            var baseName = Path.GetFileNameWithoutExtension(high).Replace("_high", string.Empty, StringComparison.OrdinalIgnoreCase);
            _status.Text = $"{baseName} · {data.Width}×{data.Height}×{data.Depth} · 高低位合成 16 位";
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
        finally { EndLoad(); }
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

    private void BeginLoad(string status)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        _openButton.IsEnabled = false;
        _openMp4Button.IsEnabled = false;
        _infoButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _status.Text = status;
    }

    private void EndLoad()
    {
        _openButton.IsEnabled = true;
        _openMp4Button.IsEnabled = true;
        _infoButton.IsEnabled = true;
        _cancelButton.IsEnabled = false;
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
        if (_disposed) return;
        EnsureRenderer();
        VolumeData? pending;
        lock (_volumeGate) { pending = _pendingVolume; _pendingVolume = null; }
        if (pending != null) UploadVolume(pending);

        var dpi = VisualTreeHelper.GetDpi(_glControl);
        var width = Math.Max(1, (int)Math.Ceiling(_glControl.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(_glControl.ActualHeight * dpi.DpiScaleY));
        GL.Viewport(0, 0, width, height);
        GL.ClearColor(0.035f, 0.04f, 0.055f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (_volume == null || _volumeTexture == 0) return;

        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.UseProgram(_program);
        GL.BindVertexArray(_vertexArray);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _volumeTexture);
        GL.Uniform1(GL.GetUniformLocation(_program, "uVolume"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "uThreshold"), _threshold);
        GL.Uniform1(GL.GetUniformLocation(_program, "uOpacity"), _opacity);

        var largest = Math.Max(_volume.Width, Math.Max(_volume.Height, _volume.Depth));
        var model = Matrix4.CreateScale(_volume.Width / (float)largest, _volume.Height / (float)largest, _volume.Depth / (float)largest);
        var camera = new Vector3(
            _distance * MathF.Cos(_pitch) * MathF.Sin(_yaw),
            _distance * MathF.Sin(_pitch),
            _distance * MathF.Cos(_pitch) * MathF.Cos(_yaw));
        var view = Matrix4.LookAt(camera, Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(42f), width / (float)height, 0.05f, 20f);
        var mvp = model * view * projection;
        Matrix4.Invert(model, out var inverseModel);
        var cameraObject = Vector3.TransformPosition(camera, inverseModel);
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uMvp"), true, ref mvp);
        GL.Uniform3(GL.GetUniformLocation(_program, "uCameraObject"), cameraObject);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 36);
        GL.BindVertexArray(0);
    }

    private void EnsureRenderer()
    {
        if (_program != 0) return;
        _program = CreateProgram(VertexShader, FragmentShader);
        var vertices = CubeVertices;
        _vertexArray = GL.GenVertexArray();
        _vertexBuffer = GL.GenBuffer();
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    private void UploadVolume(VolumeData data)
    {
        if (_volumeTexture != 0) GL.DeleteTexture(_volumeTexture);
        _volumeTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, _volumeTexture);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16, data.Width, data.Height, data.Depth,
            0, OpenTK.Graphics.OpenGL4.PixelFormat.Red, PixelType.UnsignedShort, data.Voxels);
        _volume = data with { Voxels = [] };
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _rotating = true;
        _lastMouse = e.GetPosition(_glControl);
        _glControl.CaptureMouse();
    }
    private void OnMouseUp(object sender, MouseButtonEventArgs e) { _rotating = false; _glControl.ReleaseMouseCapture(); }
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_rotating) return;
        var point = e.GetPosition(_glControl);
        _yaw += (float)(point.X - _lastMouse.X) * 0.008f;
        _pitch = Math.Clamp(_pitch + (float)(point.Y - _lastMouse.Y) * 0.008f, -1.45f, 1.45f);
        _lastMouse = point;
        _glControl.InvalidateVisual();
    }
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.88f : 1.14f), 1.1f, 8f);
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

    private const string VertexShader = """
        #version 330 core
        layout(location=0) in vec3 aPosition;
        uniform mat4 uMvp;
        out vec3 vPosition;
        void main() { vPosition = aPosition; gl_Position = uMvp * vec4(aPosition, 1.0); }
        """;

    private const string FragmentShader = """
        #version 330 core
        in vec3 vPosition;
        out vec4 fragColor;
        uniform sampler3D uVolume;
        uniform vec3 uCameraObject;
        uniform float uThreshold;
        uniform float uOpacity;
        void main() {
            vec3 ray = normalize(vPosition - uCameraObject);
            vec3 p = vPosition + ray * 0.001;
            vec4 accum = vec4(0.0);
            const float stepSize = 0.0035;
            for (int i = 0; i < 900; ++i) {
                if (any(greaterThan(abs(p), vec3(0.501)))) break;
                float value = texture(uVolume, p + vec3(0.5)).r;
                float density = max(0.0, (value - uThreshold) / max(0.001, 1.0 - uThreshold));
                float alpha = 1.0 - exp(-density * uOpacity * 7.0 * stepSize);
                vec3 color = mix(vec3(0.08, 0.35, 0.78), vec3(0.92, 0.96, 1.0), density);
                accum.rgb += (1.0 - accum.a) * alpha * color;
                accum.a += (1.0 - accum.a) * alpha;
                if (accum.a > 0.985) break;
                p += ray * stepSize;
            }
            if (accum.a < 0.005) discard;
            fragColor = accum;
        }
        """;

    private static readonly float[] CubeVertices =
    [
        -.5f,-.5f,-.5f,  -.5f,.5f,-.5f,  .5f,.5f,-.5f,  -.5f,-.5f,-.5f,  .5f,.5f,-.5f,  .5f,-.5f,-.5f,
        -.5f,-.5f,.5f,   .5f,-.5f,.5f,   .5f,.5f,.5f,   -.5f,-.5f,.5f,   .5f,.5f,.5f,   -.5f,.5f,.5f,
        -.5f,.5f,-.5f,   -.5f,.5f,.5f,   .5f,.5f,.5f,   -.5f,.5f,-.5f,   .5f,.5f,.5f,   .5f,.5f,-.5f,
        -.5f,-.5f,-.5f,  .5f,-.5f,-.5f,  .5f,-.5f,.5f,  -.5f,-.5f,-.5f,  .5f,-.5f,.5f,  -.5f,-.5f,.5f,
        -.5f,-.5f,-.5f,  -.5f,-.5f,.5f,  -.5f,.5f,.5f,  -.5f,-.5f,-.5f,  -.5f,.5f,.5f,  -.5f,.5f,-.5f,
        .5f,-.5f,-.5f,   .5f,.5f,-.5f,   .5f,.5f,.5f,   .5f,-.5f,-.5f,   .5f,.5f,.5f,   .5f,-.5f,.5f
    ];
}
