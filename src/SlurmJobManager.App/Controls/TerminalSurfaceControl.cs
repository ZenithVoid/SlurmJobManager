using System.Globalization;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XTerm;
using XTerm.Buffer;
using XTerm.Common;
using XTerm.Input;
using XTerm.Options;

namespace SlurmJobManager.App.Controls;

public sealed class TerminalResizedEventArgs : EventArgs
{
    public int Cols { get; }
    public int Rows { get; }

    public TerminalResizedEventArgs(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
    }
}

public sealed class TerminalSurfaceControl : FrameworkElement, IDisposable
{
    private static readonly Color DefaultForeground = Color.FromRgb(0xD9, 0xE1, 0xE8);
    private static readonly Color DefaultBackground = Color.FromRgb(0x0F, 0x14, 0x1A);
    private static readonly Color CursorColor = Color.FromArgb(180, 0x9D, 0xCB, 0xFF);
    private static readonly Typeface MonoTypeface = new(new FontFamily("Cascadia Mono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly TimeSpan MinRenderInterval = TimeSpan.FromMilliseconds(16);
    private const double PixelsPerDipTolerance = 0.0001;

    private readonly Terminal _terminal;
    private readonly DispatcherTimer _renderTimer;
    private readonly VisualCollection _visuals;
    private readonly List<DrawingVisual> _rowVisuals = new();
    private readonly DrawingVisual _cursorVisual = new();
    private readonly SolidColorBrush _defaultForegroundBrush;
    private readonly SolidColorBrush _defaultBackgroundBrush;
    private readonly SolidColorBrush _cursorBrush;
    private readonly Dictionary<uint, SolidColorBrush> _brushCache = new();
    private readonly int _fontSize = 14;
    private ulong[] _rowHashes = Array.Empty<ulong>();
    private int _renderQueued;
    private double _cellWidth;
    private double _cellHeight;
    private double _pixelsPerDip = 1d;
    private bool _isDisposed;
    private bool _fullRenderRequested = true;
    private bool _lastCursorVisible;
    private int _lastCursorX = -1;
    private int _lastCursorY = -1;

    public event EventHandler<string>? InputGenerated;
    public event EventHandler<TerminalResizedEventArgs>? TerminalResized;

    public Terminal Terminal => _terminal;

    public TerminalSurfaceControl()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;

        _visuals = new VisualCollection(this);

        _defaultForegroundBrush = CreateFrozenBrush(DefaultForeground);
        _defaultBackgroundBrush = CreateFrozenBrush(DefaultBackground);
        _cursorBrush = CreateFrozenBrush(CursorColor);

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = MinRenderInterval
        };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            if (_isDisposed)
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                return;
            }

            Interlocked.Exchange(ref _renderQueued, 0);
            RenderPendingChanges();
        };

        _terminal = new Terminal(new TerminalOptions
        {
            Cols = 120,
            Rows = 36,
            Scrollback = 5000,
            TermName = "xterm-256color",
            CursorStyle = CursorStyle.Block,
            CursorBlink = true,
            FontFamily = "Cascadia Mono",
            FontSize = _fontSize,
        });

        _terminal.LineFed += (_, _) => RequestRender();
        _terminal.BufferChanged += (_, _) => RequestRender();
        _terminal.Resized += (_, _) =>
        {
            EnsureRowVisuals(_terminal.Rows, true);
            RequestRender(forceFull: true);
        };
        _terminal.Scrolled += (_, _) => RequestRender(forceFull: true);
        _terminal.CursorStyleChanged += (_, _) => RequestRender();

        Loaded += (_, _) =>
        {
            ResizeToViewport();
            Focus();
        };
        SizeChanged += (_, _) =>
        {
            ResizeToViewport();
            InvalidateVisual();
        };

        EnsureRowVisuals(_terminal.Rows, true);
    }

    public void Write(string data)
    {
        if (string.IsNullOrEmpty(data) || _isDisposed) return;
        _terminal.Write(data);
        RequestRender();
    }

    public void Clear()
    {
        if (_isDisposed) return;
        _terminal.Clear();
        RequestRender(forceFull: true);
    }

    public string GetVisibleText()
        => string.Join(Environment.NewLine, _terminal.GetVisibleLines());

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_isDisposed) return;

        var modifiers = ConvertModifiers(Keyboard.Modifiers);

        if (TryMapCtrlChord(e.Key, Keyboard.Modifiers, out var ctrlData))
        {
            EmitInput(ctrlData);
            e.Handled = true;
            return;
        }

        if (TryMapKey(e.Key, out var terminalKey))
        {
            var data = _terminal.GenerateKeyInput(terminalKey, modifiers);
            EmitInput(data);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_isDisposed || string.IsNullOrEmpty(e.Text)) return;

        var modifiers = ConvertModifiers(Keyboard.Modifiers);
        foreach (var ch in e.Text)
        {
            var data = _terminal.GenerateCharInput(ch, modifiers);
            EmitInput(data);
        }
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(_defaultBackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    private void ResizeToViewport()
    {
        if (_isDisposed || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        EnsureCellMetrics();
        var cellWidth = _cellWidth;
        var cellHeight = _cellHeight;
        if (cellWidth <= 0 || cellHeight <= 0) return;

        var cols = Math.Max(2, (int)(ActualWidth / cellWidth));
        var rows = Math.Max(2, (int)(ActualHeight / cellHeight));
        if (cols == _terminal.Cols && rows == _terminal.Rows) return;

        _terminal.Resize(cols, rows);
        TerminalResized?.Invoke(this, new TerminalResizedEventArgs(cols, rows));
        RequestRender(forceFull: true);
    }

    private void EnsureCellMetrics()
    {
        var currentPixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_cellWidth > 0 && _cellHeight > 0 && Math.Abs(_pixelsPerDip - currentPixelsPerDip) < PixelsPerDipTolerance)
            return;

        _pixelsPerDip = currentPixelsPerDip;
        var probe = new FormattedText(
            "W",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            _fontSize,
            Brushes.White,
            _pixelsPerDip);
        _cellWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Max(1, probe.Height);
        _fullRenderRequested = true;
    }

    private void RenderPendingChanges()
    {
        if (_isDisposed)
            return;

        EnsureCellMetrics();
        EnsureRowVisuals(_terminal.Rows, false);

        var buffer = _terminal.Buffer;
        var rows = _terminal.Rows;
        var cols = _terminal.Cols;
        if (_rowHashes.Length != rows)
            EnsureRowVisuals(rows, true);
        var yDisp = buffer.YDisp;
        var forceFull = _fullRenderRequested;
        _fullRenderRequested = false;

        for (var row = 0; row < rows; row++)
        {
            var line = buffer.GetLine(yDisp + row);
            var hash = ComputeLineHash(line, cols);
            if (!forceFull && _rowHashes[row] == hash)
                continue;

            _rowHashes[row] = hash;
            DrawRow(row, line, cols);
        }

        DrawCursor(buffer, cols, rows);
    }

    private void EnsureRowVisuals(int rows, bool forceReset)
    {
        if (!forceReset && _rowVisuals.Count == rows)
            return;

        _visuals.Clear();
        _rowVisuals.Clear();

        for (var i = 0; i < rows; i++)
        {
            var visual = new DrawingVisual();
            _rowVisuals.Add(visual);
            _visuals.Add(visual);
        }

        _visuals.Add(_cursorVisual);
        _rowHashes = new ulong[rows];
        for (var i = 0; i < _rowHashes.Length; i++)
            _rowHashes[i] = ulong.MaxValue;

        _lastCursorVisible = false;
        _lastCursorX = -1;
        _lastCursorY = -1;
        _fullRenderRequested = true;
    }

    private void DrawRow(int row, BufferLine? line, int cols)
    {
        var visual = _rowVisuals[row];
        using var dc = visual.RenderOpen();

        var y = row * _cellHeight;
        dc.DrawRectangle(_defaultBackgroundBrush, null, new Rect(0, y, ActualWidth, _cellHeight));

        if (line == null)
            return;

        var pixelsPerDip = _pixelsPerDip;
        for (var col = 0; col < Math.Min(cols, line.Length); col++)
        {
            var cell = line[col];
            if (cell.Width == 0) continue;

            var attrs = cell.Attributes;
            var (fg, bg) = ResolveColors(attrs);
            var rect = new Rect(col * _cellWidth, y, Math.Max(_cellWidth * cell.Width, _cellWidth), _cellHeight);

            if (bg != DefaultBackground)
                dc.DrawRectangle(GetBrush(bg), null, rect);

            var content = string.IsNullOrEmpty(cell.Content) ? " " : cell.Content;
            var formatted = new FormattedText(
                content,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                _fontSize,
                fg == DefaultForeground ? _defaultForegroundBrush : GetBrush(fg),
                pixelsPerDip);
            dc.DrawText(formatted, new Point(rect.X, rect.Y));
        }
    }

    private void DrawCursor(TerminalBuffer buffer, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            _lastCursorVisible = false;
            _lastCursorX = -1;
            _lastCursorY = -1;
            using var clearDc = _cursorVisual.RenderOpen();
            return;
        }

        var cursorVisible = _terminal.CursorVisible;
        var cursorX = Math.Clamp(buffer.X, 0, cols - 1);
        var cursorY = Math.Clamp(buffer.Y, 0, rows - 1);

        if (_lastCursorVisible == cursorVisible && _lastCursorX == cursorX && _lastCursorY == cursorY)
            return;

        _lastCursorVisible = cursorVisible;
        _lastCursorX = cursorX;
        _lastCursorY = cursorY;

        using var dc = _cursorVisual.RenderOpen();
        if (!cursorVisible)
            return;

        var cursorRect = new Rect(cursorX * _cellWidth, cursorY * _cellHeight, _cellWidth, _cellHeight);
        dc.DrawRectangle(_cursorBrush, null, cursorRect);
    }

    // FNV-1a is used here for a fast per-row fingerprint so unchanged rows skip redraw; a rare collision only causes a missed single-frame update.
    private static ulong ComputeLineHash(BufferLine? line, int cols)
    {
        const ulong offset = 14695981039346656037ul;
        const ulong prime = 1099511628211ul;
        var hash = offset;

        if (line == null)
            return hash;

        var length = Math.Min(cols, line.Length);
        for (var col = 0; col < length; col++)
        {
            var cell = line[col];
            hash ^= (ulong)cell.Width;
            hash *= prime;

            var attrs = cell.Attributes;
            hash ^= (ulong)attrs.GetFgColor();
            hash *= prime;
            hash ^= (ulong)attrs.GetBgColor();
            hash *= prime;
            hash ^= (ulong)attrs.GetFgColorMode();
            hash *= prime;
            hash ^= (ulong)attrs.GetBgColorMode();
            hash *= prime;
            hash ^= attrs.IsInverse() ? 1ul : 0ul;
            hash *= prime;

            var content = cell.Content;
            if (string.IsNullOrEmpty(content))
                continue;

            for (var i = 0; i < content.Length; i++)
            {
                hash ^= content[i];
                hash *= prime;
            }
        }

        return hash;
    }

    private static (Color fg, Color bg) ResolveColors(AttributeData attrs)
    {
        var fg = ResolveColor(attrs.GetFgColor(), attrs.GetFgColorMode(), DefaultForeground);
        var bg = ResolveColor(attrs.GetBgColor(), attrs.GetBgColorMode(), DefaultBackground);
        if (attrs.IsInverse()) (fg, bg) = (bg, fg);
        return (fg, bg);
    }

    private static Color ResolveColor(int colorValue, int mode, Color fallback)
        => mode switch
        {
            1 => ResolveAnsi256Color(colorValue),
            2 => Color.FromRgb((byte)((colorValue >> 16) & 0xFF), (byte)((colorValue >> 8) & 0xFF), (byte)(colorValue & 0xFF)),
            _ => fallback
        };

    private static Color ResolveAnsi256Color(int value)
    {
        var index = Math.Clamp(value, 0, 255);
        if (index < 16)
        {
            return index switch
            {
                0 => Color.FromRgb(0x00, 0x00, 0x00),
                1 => Color.FromRgb(0xCD, 0x31, 0x31),
                2 => Color.FromRgb(0x0D, 0xBC, 0x79),
                3 => Color.FromRgb(0xE5, 0xE5, 0x10),
                4 => Color.FromRgb(0x24, 0x72, 0xC8),
                5 => Color.FromRgb(0xBC, 0x3F, 0xBC),
                6 => Color.FromRgb(0x11, 0xA8, 0xCD),
                7 => Color.FromRgb(0xE5, 0xE5, 0xE5),
                8 => Color.FromRgb(0x66, 0x66, 0x66),
                9 => Color.FromRgb(0xF1, 0x4C, 0x4C),
                10 => Color.FromRgb(0x23, 0xD1, 0x8B),
                11 => Color.FromRgb(0xF5, 0xF5, 0x43),
                12 => Color.FromRgb(0x3B, 0x8E, 0xF3),
                13 => Color.FromRgb(0xD6, 0x70, 0xD6),
                14 => Color.FromRgb(0x29, 0xB8, 0xDB),
                _ => Color.FromRgb(0xFF, 0xFF, 0xFF),
            };
        }

        if (index <= 231)
        {
            var valueIndex = index - 16;
            var r = valueIndex / 36;
            var g = (valueIndex % 36) / 6;
            var b = valueIndex % 6;
            return Color.FromRgb(ToAnsiCubeByte(r), ToAnsiCubeByte(g), ToAnsiCubeByte(b));
        }

        var gray = (byte)(8 + ((index - 232) * 10));
        return Color.FromRgb(gray, gray, gray);
    }

    private static byte ToAnsiCubeByte(int step)
        => step == 0 ? (byte)0 : (byte)(55 + (step * 40));

    private static KeyModifiers ConvertModifiers(ModifierKeys modifiers)
    {
        var result = KeyModifiers.None;
        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) result |= KeyModifiers.Shift;
        if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) result |= KeyModifiers.Alt;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control) result |= KeyModifiers.Control;
        return result;
    }

    private static bool TryMapKey(System.Windows.Input.Key key, out XTerm.Input.Key mapped)
    {
        XTerm.Input.Key? resolved = key switch
        {
            System.Windows.Input.Key.Enter => XTerm.Input.Key.Enter,
            System.Windows.Input.Key.Tab => XTerm.Input.Key.Tab,
            System.Windows.Input.Key.Back => XTerm.Input.Key.Backspace,
            System.Windows.Input.Key.Escape => XTerm.Input.Key.Escape,
            System.Windows.Input.Key.Left => XTerm.Input.Key.LeftArrow,
            System.Windows.Input.Key.Right => XTerm.Input.Key.RightArrow,
            System.Windows.Input.Key.Up => XTerm.Input.Key.UpArrow,
            System.Windows.Input.Key.Down => XTerm.Input.Key.DownArrow,
            System.Windows.Input.Key.Delete => XTerm.Input.Key.Delete,
            System.Windows.Input.Key.Home => XTerm.Input.Key.Home,
            System.Windows.Input.Key.End => XTerm.Input.Key.End,
            System.Windows.Input.Key.PageUp => XTerm.Input.Key.PageUp,
            System.Windows.Input.Key.PageDown => XTerm.Input.Key.PageDown,
            System.Windows.Input.Key.Insert => XTerm.Input.Key.Insert,
            _ => (XTerm.Input.Key?)null
        };
        if (resolved.HasValue)
        {
            mapped = resolved.Value;
            return true;
        }

        mapped = default;
        return false;
    }

    private void EmitInput(string? data)
    {
        if (!string.IsNullOrEmpty(data))
            InputGenerated?.Invoke(this, data);
    }

    private static bool TryMapCtrlChord(System.Windows.Input.Key key, ModifierKeys modifiers, out string? data)
    {
        data = null;
        if ((modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            return false;
        if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            return false;
        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            return false;

        if (key is >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z)
        {
            var letterIndex = key - System.Windows.Input.Key.A + 1;
            data = new string((char)letterIndex, 1);
            return true;
        }

        if (key == System.Windows.Input.Key.D2 || key == System.Windows.Input.Key.Space)
        {
            data = "\u0000";
            return true;
        }

        return false;
    }

    private void RequestRender(bool forceFull = false)
    {
        if (_isDisposed)
            return;

        if (forceFull)
            _fullRenderRequested = true;

        if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) != 0)
            return;

        if (!_renderTimer.IsEnabled)
            _renderTimer.Start();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _renderTimer.Stop();
        _terminal.Dispose();
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private SolidColorBrush GetBrush(Color color)
    {
        var key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (_brushCache.TryGetValue(key, out var cached))
            return cached;

        var brush = CreateFrozenBrush(color);
        _brushCache[key] = brush;
        return brush;
    }
}
