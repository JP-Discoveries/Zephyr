using CommunityToolkit.Mvvm.Input;

namespace Zephyr.UI.ViewModels;

/// <summary>
/// A user-facing command in the central registry — the single source for both the
/// customizable toolbar and the rebindable hotkeys.
/// </summary>
public sealed class AppCommand
{
    public required string Id { get; init; }            // stable key used in settings
    public required string Name { get; init; }
    public string Glyph { get; init; } = "";            // Segoe Fluent Icons glyph
    public required IRelayCommand Command { get; init; }
    public string DefaultGesture { get; init; } = "";   // canonical, e.g. "Ctrl+Shift+N"
    public bool ToolbarEligible { get; init; }          // can be placed on the toolbar
    public bool DefaultOnToolbar { get; init; }         // shown on the toolbar out of the box
}
