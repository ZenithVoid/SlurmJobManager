using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

    private readonly Terminal _terminal;
    private readonly double _fontSize = 14d;
    private bool _isDisposed;

    public event EventHandler<string>? InputGenerated;
    public event EventHandler<TerminalResizedEventArgs>? TerminalResized;

    public Terminal Terminal => _terminal;

    public TerminalSurfaceControl()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;

        _terminal = new Terminal(new TerminalOptions
        {
            Cols = 120,
            Rows = 36,
            Scrollback = 5000,
            TermName = "xterm-256color",
            CursorStyle = CursorStyle.Block,
            CursorBlink = true,
            FontFamily = "Cascadia Mono",
            FontSize = (int)_fontSize,
        });

        _terminal.LineFed += (_, _) => InvalidateVisual();
        _terminal.BufferChanged += (_, _) => InvalidateVisual();
        _terminal.Resized += (_, _) => InvalidateVisual();
        _terminal.Scrolled += (_, _) => InvalidateVisual();
        _terminal.CursorStyleChanged += (_, _) => InvalidateVisual();

        Loaded += (_, _) =>
        {
            ResizeToViewport();
            Focus();
        };
        SizeChanged += (_, _) => ResizeToViewport();
    }

    public void Write(string data)
    {
        if (string.IsNullOrEmpty(data) || _isDisposed) return;
        _terminal.Write(data);
        InvalidateVisual();
    }

    public void Clear()
    {
        if (_isDisposed) return;
        _terminal.Clear();
        InvalidateVisual();
    }

    public string GetVisibleText()
        => string.Join(Environment.NewLine, _terminal.GetVisibleLines());

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_isDisposed) return;

        var modifiers = ConvertModifiers(Keyboard.Modifiers);

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            e.Key == System.Windows.Input.Key.C)
        {
            EmitInput("\u0003");
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            e.Key == System.Windows.Input.Key.L)
        {
            EmitInput("\u000c");
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
        dc.DrawRectangle(new SolidColorBrush(DefaultBackground), null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_isDisposed) return;
        var (cellWidth, cellHeight) = MeasureCell();
        var buffer = _terminal.Buffer;
        var rows = _terminal.Rows;
        var cols = _terminal.Cols;
        var yDisp = buffer.YDisp;

        var defaultFgBrush = new SolidColorBrush(DefaultForeground);

        for (var row = 0; row < rows; row++)
        {
            var line = buffer.GetLine(yDisp + row);
            if (line == null) continue;

            for (var col = 0; col < Math.Min(cols, line.Length); col++)
            {
                var cell = line[col];
                if (cell.Width == 0) continue;

                var attrs = cell.Attributes;
                var (fg, bg) = ResolveColors(attrs);
                var rect = new Rect(col * cellWidth, row * cellHeight, Math.Max(cellWidth * cell.Width, cellWidth), cellHeight);

                if (bg != DefaultBackground)
                    dc.DrawRectangle(new SolidColorBrush(bg), null, rect);

                var content = string.IsNullOrEmpty(cell.Content) ? " " : cell.Content;
                var formatted = new FormattedText(
                    content,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    MonoTypeface,
                    _fontSize,
                    fg == DefaultForeground ? defaultFgBrush : new SolidColorBrush(fg),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(formatted, new Point(rect.X, rect.Y));
            }
        }

        if (_terminal.CursorVisible)
        {
            var cursorX = Math.Clamp(buffer.X, 0, cols - 1);
            var cursorY = Math.Clamp(buffer.Y, 0, rows - 1);
            var cursorRect = new Rect(cursorX * cellWidth, cursorY * cellHeight, cellWidth, cellHeight);
            dc.DrawRectangle(new SolidColorBrush(CursorColor), null, cursorRect);
        }
    }

    private void ResizeToViewport()
    {
        if (_isDisposed || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        var (cellWidth, cellHeight) = MeasureCell();
        if (cellWidth <= 0 || cellHeight <= 0) return;

        var cols = Math.Max(2, (int)(ActualWidth / cellWidth));
        var rows = Math.Max(2, (int)(ActualHeight / cellHeight));
        if (cols == _terminal.Cols && rows == _terminal.Rows) return;

        _terminal.Resize(cols, rows);
        TerminalResized?.Invoke(this, new TerminalResizedEventArgs(cols, rows));
        InvalidateVisual();
    }

    private (double cellWidth, double cellHeight) MeasureCell()
    {
        var probe = new FormattedText(
            "W",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            _fontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return (Math.Max(1, probe.WidthIncludingTrailingWhitespace), Math.Max(1, probe.Height));
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
        mapped = key switch
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
            _ => default
        };
        return key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Tab or System.Windows.Input.Key.Back
            or System.Windows.Input.Key.Escape or System.Windows.Input.Key.Left or System.Windows.Input.Key.Right
            or System.Windows.Input.Key.Up or System.Windows.Input.Key.Down or System.Windows.Input.Key.Delete
            or System.Windows.Input.Key.Home or System.Windows.Input.Key.End or System.Windows.Input.Key.PageUp
            or System.Windows.Input.Key.PageDown or System.Windows.Input.Key.Insert;
    }

    private void EmitInput(string? data)
    {
        if (!string.IsNullOrEmpty(data))
            InputGenerated?.Invoke(this, data);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _terminal.Dispose();
    }
}
