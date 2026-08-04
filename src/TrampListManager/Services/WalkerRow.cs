using System.Windows;
using System.Windows.Media;

namespace TrampListManager.Services;

/// <summary>
/// A row in the Tramplers list: the file, plus whatever we could learn about it.
///
/// Three tiers of knowledge, in descending order of usefulness:
///   1. Published on TrampList — full metadata, resolved by file hash.
///   2. Labelled locally — a name the user typed for something they built themselves.
///   3. Neither — falls back to the short UUID, which is all the file itself offers.
/// </summary>
public sealed record WalkerRow(WalkerFile File, DesignInfo? Design, LocalLabel? Label)
{
    /// <summary>What to show as the design's name.</summary>
    public string Title =>
        Design?.Name
        ?? Label?.Name
        ?? $"Untitled  ({File.ShortId})";

    /// <summary>The line underneath: chassis and crew if known, otherwise file facts.</summary>
    public string Detail =>
        Design?.Summary
        ?? Label?.Notes
        ?? "Not on TrampList — click Rename to label it";

    /// <summary>Right-hand column: where this design came from.</summary>
    public string Source => Design is not null ? "TrampList" : (Label is not null ? "Labelled" : "Local");

    public bool IsPublished => Design is not null;

    /// <summary>
    /// Rust for designs the site knows, muted for the rest — so a glance down the column
    /// separates published designs from local ones without reading the words.
    /// </summary>
    public Brush SourceBrush =>
        (Brush)Application.Current.FindResource(Design is not null ? "Primary" : "Muted");

    public string SizeText => File.SizeText;
    public string ModifiedText => File.ModifiedText;
    public string ShortId => File.ShortId;
}
