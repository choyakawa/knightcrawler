using Literals = Producer.Features.CrawlerSupport.Literals;

namespace Producer.Features.Crawlers.Torrentio;

public partial class TorrentioCrawler(
    IHttpClientFactory httpClientFactory,
    ILogger<TorrentioCrawler> logger,
    IDataStorage storage,
    TorrentioConfiguration configuration) : BaseCrawler(logger, storage)
{
    [GeneratedRegex(@"(\d+(\.\d+)?) (GB|MB)")]
    private static partial Regex SizeMatcher();
    private const int MaximumEmptyItemsCount = 5;

    private const string MovieSlug = "movie/{0}.json";
    protected override string Url => "sort=size%7Cqualityfilter=other,scr,cam,unknown/stream/{0}";
    protected override IReadOnlyDictionary<string, string> Mappings { get; } = new Dictionary<string, string>();
    protected override string Source  => "Torrentio";
    private readonly Dictionary<string, TorrentioScrapeInstance> _instanceStates = [];
    public override async Task Execute()
    {
        var client = httpClientFactory.CreateClient(Literals.CrawlerClient);
        var instances = configuration.Instances;
        var totalRecordCount = await Storage.GetRowCountImdbMetadata();

        if (!totalRecordCount.IsSuccess)
        {
            logger.LogError("Failed to get total record count from metadata");
            return;
        }
        
        logger.LogInformation("Total IMDB records to process: {TotalRecordCount}", totalRecordCount.Success);
        var tasks = instances.Select(x => ProcessForInstanceAsync(x, client, totalRecordCount.Success)).ToArray();
        await Task.WhenAll(tasks);
    }

private Task ProcessForInstanceAsync(TorrentioInstance instance, HttpClient client, long totalRecordCount) =>
    Task.Run(
        async () =>
        {
            var emptyMetaDbItemsCount = 0;

            var state = instance.EnsureStateExists(_instanceStates);

            while (state.TotalProcessed < totalRecordCount)
            {
                logger.LogInformation("Processing {TorrentioInstance}", instance.Name);
                logger.LogInformation("Current processed requests: {ProcessedRequests}", state.TotalProcessed);

                var items = await Storage.GetImdbEntriesForRequests(
                    DateTime.UtcNow.Year,
                    instance.RateLimit.BatchSize,
                    state.LastProcessedImdbId);

                if (items.Count == 0)
                {
                    emptyMetaDbItemsCount++;
                    logger.LogInformation("No items to process for {TorrentioInstance}", instance.Name);

                    if (emptyMetaDbItemsCount >= MaximumEmptyItemsCount)
                    {
                        logger.LogInformation("Maximum empty document count reached. Cancelling {TorrentioInstance}", instance.Name);
                        break;
                    }

                    continue;
                }

                var newTorrents = new ConcurrentBag<IngestedTorrent>();
                var processedItemsCount = 0;

                await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (item, ct) =>
                {
                    try
                    {
                        var torrentInfo = await ScrapeInstance(instance, item.ImdbId, client);

                        if (torrentInfo is not null)
                        {
                            foreach (var torrent in torrentInfo.Where(x => x != null))
                            {
                                newTorrents.Add(torrent!);
                            }
                        }

                        Interlocked.Increment(ref processedItemsCount);
                    }
                    catch (Exception)
                    {
                        logger.LogWarning("Error processing item {ImdbId} for {TorrentioInstance}", item.ImdbId, instance.Name);
                        instance.SetPossiblyRateLimited(state);
                    }
                });

                if (newTorrents.Count > 0)
                {
                    await InsertTorrents(newTorrents.ToList());
                }

                state.LastProcessedImdbId = items[^1].ImdbId;
            }
        });


    private void SetupResiliencyPolicyForInstance(TorrentioInstance instance, TorrentioScrapeInstance state)
    {
        var retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: 2, // initial attempt + 2 retries
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timeSpan, retryCount, _) =>
                {
                    logger.LogWarning("Retry {RetryCount} encountered an exception: {Message}. Pausing for {Timespan} seconds instance {TorrentioInstance}", retryCount, exception.Message, timeSpan.Seconds, instance.Name);
                });

        var circuitBreakerPolicy = Policy
            .Handle<Exception>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: instance.RateLimit.ExceptionLimit,
                durationOfBreak: TimeSpan.FromSeconds(instance.RateLimit.ExceptionIntervalInSeconds),
                onBreak: (ex, breakDelay) =>
                {
                    logger.LogWarning(ex, "Breaking circuit for {TorrentioInstance} for {BreakDelay}ms due to {Exception}", instance.Name, breakDelay.TotalMilliseconds, ex.Message);
                },
                onReset: () => logger.LogInformation("Circuit closed for {TorrentioInstance}, calls will flow again", instance.Name),
                onHalfOpen: () => logger.LogInformation("Circuit is half-open for {TorrentioInstance}, next call is a trial if it should close or break again", instance.Name));

        var policyWrap = Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);

        state.ResiliencyPolicy = policyWrap;
    }

    private async Task<List<IngestedTorrent?>?> ScrapeInstance(TorrentioInstance instance, string imdbId, HttpClient client)
    {
        logger.LogInformation("Searching Torrentio {TorrentioInstance}: {ImdbId}", instance.Name, imdbId);
        var movieSlug = string.Format(MovieSlug, imdbId);
        var urlSlug = string.Format(Url, movieSlug);
        return await RunRequest(instance, urlSlug, imdbId, client);
    }

    private async Task<List<IngestedTorrent?>?> RunRequest(TorrentioInstance instance, string urlSlug, string imdbId, HttpClient client)
    {
        var requestUrl = $"{instance.Url}/{urlSlug}";
        var response = await client.GetAsync(requestUrl);

        if (!response.IsSuccessStatusCode)
        {
            throw new("Failed to fetch " + requestUrl);
        }

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var streams = json.RootElement.GetProperty("streams").EnumerateArray();
        return streams.Select(x => ParseTorrent(instance, x, imdbId)).Where(x => x != null).ToList();
    }

    private IngestedTorrent? ParseTorrent(TorrentioInstance instance, JsonElement item, string imdId)
    {
        var title = item.GetProperty("title").GetString();
        var infoHash = item.GetProperty("infoHash").GetString();

        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(infoHash))
        {
            return null;
        }

        var torrent = ParseTorrentDetails(title, instance, infoHash, imdId);

        return string.IsNullOrEmpty(torrent.Name) ? null : torrent;
    }

    private IngestedTorrent ParseTorrentDetails(string title, TorrentioInstance instance, string infoHash, string imdbId)
    {
        var torrent = new IngestedTorrent
        {
            Source = $"{Source}_{instance.Name}",
            InfoHash = infoHash,
            Category = "movies", // we only handle movies for now...
        };

        var span = title.AsSpan();
        var titleEnd = span.IndexOf('\n');
        var titlePart = titleEnd >= 0 ? span[..titleEnd].ToString() : title;

        torrent.Name = titlePart.Replace('.', ' ').TrimEnd('.');

        var sizeMatch = SizeMatcher().Match(title);

        if (sizeMatch.Success)
        {
            var size = double.Parse(sizeMatch.Groups[1].Value); // Size Value
            var sizeUnit = sizeMatch.Groups[3].Value; // Size Unit (GB/MB)

            var sizeInBytes = sizeUnit switch
            {
                "GB" => (long) (size * 1073741824),
                "MB" => (long) (size * 1048576),
                _ => 0,
            };

            torrent.Size = sizeInBytes.ToString();
        }

        return torrent;
    }
}
