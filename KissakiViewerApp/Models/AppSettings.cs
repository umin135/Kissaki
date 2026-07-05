namespace KissakiViewer.Models;

public sealed class AppSettings
{
    /// <summary>Root directory for exported assets. Empty string means default (&lt;exe dir&gt;/export).</summary>
    public string ExportDirectory { get; set; } = "";

    /// <summary>When true, exported files use the recovered asset name instead of the KTID hex.</summary>
    public bool UseRestoredName { get; set; } = false;
}
