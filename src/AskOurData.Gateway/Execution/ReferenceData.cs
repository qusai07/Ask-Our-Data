namespace AskOurData.Gateway.Execution;

public sealed record CodeRef(int Id, string Code, string Name);

/// <summary>Region and segment allowlists, loaded from rpt views at startup.</summary>
public sealed class ReferenceData
{
    public IReadOnlyList<CodeRef> Regions { get; private set; } = [];
    public IReadOnlyList<CodeRef> Segments { get; private set; } = [];
    public bool Loaded { get; private set; }

    public void Set(IReadOnlyList<CodeRef> regions, IReadOnlyList<CodeRef> segments)
    {
        if (regions.Count == 0 || segments.Count == 0) throw new InvalidOperationException("Reference data is empty.");
        Regions = regions;
        Segments = segments;
        Loaded = true;
    }

    public CodeRef? Region(string code) => Regions.FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
    public CodeRef? Segment(string code) => Segments.FirstOrDefault(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}
