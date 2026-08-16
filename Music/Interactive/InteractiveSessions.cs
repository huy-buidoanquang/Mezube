namespace Mezube.Music.Interactive;

public sealed class SearchPickSession
{
    public const int MaxResults = 5;

    public long ClanId { get; set; }
    public long ChannelId { get; set; }
    public long UserId { get; set; }
    public string Mode { get; set; } = "streaming";
    public bool WantVideo { get; set; }
    public long TargetChannelId { get; set; }
    public string? TargetChannelLabel { get; set; }
    public string? TargetRoomName { get; set; }
    public string Query { get; set; } = string.Empty;
    public List<TrackCandidate> Candidates { get; set; } = [];
    public long CreatedAtUnixMs { get; set; }
}

public sealed class PlaylistImportSession
{
    public const int FetchMax = 20;
    public const int PageSize = 5;
    public const int SelectMax = 10;

    public long ClanId { get; set; }
    public long ChannelId { get; set; }
    public long UserId { get; set; }
    public long PlaylistId { get; set; }
    public string PlaylistName { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public List<TrackCandidate> Candidates { get; set; } = [];
    public List<string> SelectedTokens { get; set; } = [];
    public int Page { get; set; }
    public long CreatedAtUnixMs { get; set; }

    public int PageCount => Math.Max(1, (int)Math.Ceiling(Candidates.Count / (double)PageSize));

    public IReadOnlyList<TrackCandidate> PageCandidates()
    {
        if (Candidates.Count == 0)
        {
            return [];
        }

        var start = Math.Clamp(Page, 0, PageCount - 1) * PageSize;
        if (start >= Candidates.Count)
        {
            return [];
        }

        var take = Math.Min(PageSize, Candidates.Count - start);
        return Candidates.GetRange(start, take);
    }
}
