using SlurmJobManager.App.Converters;

namespace SlurmJobManager.App.ViewModels;

/// <summary>A single line displayed in the embedded console panel.</summary>
public sealed class ConsoleLine
{
    public string Text { get; init; } = string.Empty;
    public ConsoleLineKind Kind { get; init; } = ConsoleLineKind.Stdout;

    public static ConsoleLine Command(string text) => new() { Text = text, Kind = ConsoleLineKind.Command };
    public static ConsoleLine Stdout(string text)  => new() { Text = text, Kind = ConsoleLineKind.Stdout };
    public static ConsoleLine Stderr(string text)  => new() { Text = text, Kind = ConsoleLineKind.Stderr };
    public static ConsoleLine Error(string text)   => new() { Text = text, Kind = ConsoleLineKind.Error };
    public static ConsoleLine Meta(string text)    => new() { Text = text, Kind = ConsoleLineKind.Meta };
}
