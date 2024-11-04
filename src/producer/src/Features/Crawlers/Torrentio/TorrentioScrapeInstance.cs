namespace Producer.Features.Crawlers.Torrentio;

public class TorrentioScrapeInstance
{
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public int RequestCount { get; set; }
    public int TotalProcessed { get; set; }
    public string? LastProcessedImdbId { get; set; } = "tt21483522";
    public IAsyncPolicy? ResiliencyPolicy { get; set; }
}
