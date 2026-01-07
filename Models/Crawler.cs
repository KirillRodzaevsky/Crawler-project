using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HtmlAgilityPack;
using static Crawler_project.Models.DTO;
namespace Crawler_project.Models
{


    public enum CrawlState { Pending, Running, Finished, Canceled, Faulted } //статусы задачи

    public sealed class CrawlJob //класс с параметрами нашего кравлера
    {
        public Guid JobId { get; init; }
        public string StartUrl { get; init; } = "";
        public int MaxPages { get; init; }
        public int Workers { get; init; }
        public bool RespectRobots { get; init; }

        public CrawlState State { get; set; } = CrawlState.Pending;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string? Error { get; set; }

        public ConcurrentDictionary<string, byte> Discovered { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<string> DiscoveredOrder { get; } = new();
        public ConcurrentDictionary<string, byte> Visited { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int VisitedCount;
        public CancellationTokenSource Cts { get; } = new();
        public Task? RunTask { get; set; }
    }

    public interface JobStore
    {
        CrawlJob CreateAndStart(CrawlStartRequest req);
        CrawlJob? Get(Guid jobId);
        void Cancel(Guid jobId);
        string[] GetUrls(Guid jobId, int offset, int limit, out int total);
    }

    public sealed class InMemJobStore : JobStore
    {
        private readonly ConcurrentDictionary<Guid, CrawlJob> _jobs = new();

        private readonly Crawler _crawler = new(); 
        private readonly IHttpClientFactory _httpClientFactory;

        public InMemJobStore(IHttpClientFactory httpClientFactory)
            => _httpClientFactory = httpClientFactory;

        public CrawlJob CreateAndStart(CrawlStartRequest req)
        {
            var job = new CrawlJob
            {
                JobId = Guid.NewGuid(),
                StartUrl = req.StartUrl,
                MaxPages = req.MaxPages,
                Workers = req.Workers,
                RespectRobots = req.RespectRobots
            };

            _jobs[job.JobId] = job;

            job.RunTask = Task.Run(() => RunJobAsync(job));
            return job;
        }

        public CrawlJob? Get(Guid jobId) => _jobs.TryGetValue(jobId, out var j) ? j : null;

        public void Cancel(Guid jobId)
        {
            if (_jobs.TryGetValue(jobId, out var job))
                job.Cts.Cancel();
        }

        public string[] GetUrls(Guid jobId, int offset, int limit, out int total)
        {
            total = 0;
            if (!_jobs.TryGetValue(jobId, out var job)) return Array.Empty<string>();
            var arr = job.DiscoveredOrder.ToArray();
            total = arr.Length;

            offset = Math.Max(0, offset);
            limit = Math.Clamp(limit, 1, 5000);
            return arr.Skip(offset).Take(limit).ToArray();
        }

        private async Task RunJobAsync(CrawlJob job)//Главная асинхронная функция, не трогать если работает
        {
            job.State = CrawlState.Running;
            job.StartedAt = DateTimeOffset.Now;
            try
            {
                var http = _httpClientFactory.CreateClient("crawler");
                RobotsRules? robots = null;
                if (job.RespectRobots) robots = await RobotsRules.FetchAsync(new Uri(job.StartUrl), "MyCrawler", http);

                _crawler.MaxPages = job.MaxPages;

                var start = _crawler.Normalize(job.StartUrl);
                if (start is null) throw new Exception("Cтартовый url не распознан");

                var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();

                job.Discovered.TryAdd(start, 0);
                job.DiscoveredOrder.Enqueue(start);
                await channel.Writer.WriteAsync(start, job.Cts.Token);

                var cts = job.Cts;
                var workers = job.Workers;

                Task[] tasks = Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
                {
                    while (await channel.Reader.WaitToReadAsync(cts.Token))
                    {
                        while (channel.Reader.TryRead(out var url))
                        {
                            if (!job.Visited.TryAdd(url, 0)) continue;

                            var cur = Interlocked.Increment(ref job.VisitedCount);
                            if (cur > job.MaxPages)
                            {
                                cts.Cancel();
                                break;
                            }

                            var links = await _crawler.ParseRun(url, cts.Token);

                            foreach (var link in links)
                            {
                                var norm = _crawler.Normalize(link);
                                if (norm is null) continue;

                                if (!Uri.TryCreate(norm, UriKind.Absolute, out var u)) continue;
                                if (robots != null && !robots.IsAllowed(u)) continue;

                                if (job.Discovered.TryAdd(norm, 0))
                                {
                                    job.DiscoveredOrder.Enqueue(norm);
                                    channel.Writer.TryWrite(norm);
                                }
                            }
                        }
                    }
                }, cts.Token)).ToArray();

                await Task.WhenAll(tasks);
                channel.Writer.TryComplete();

                job.State = cts.IsCancellationRequested ? CrawlState.Canceled : CrawlState.Finished;
                job.FinishedAt = DateTimeOffset.Now;
            }
            catch (OperationCanceledException)
            {
                job.State = CrawlState.Canceled;
                job.FinishedAt = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                job.State = CrawlState.Faulted;
                job.Error = ex.Message;
                job.FinishedAt = DateTimeOffset.Now;
            }
        }
    }

    class Crawler //Тут все шаблоны, фильтры и нормализация ссылок
    {
        public int MaxPages { get; set; } = 1000;

        public async Task<string[]> ParseRun(string url, CancellationToken ct)
        {
            var normalized = Normalize(url);
            if (normalized is null) return Array.Empty<string>();

            try
            {
                var web = new HtmlWeb();
                var doc = await web.LoadFromWebAsync(normalized);

                var baseUri = new Uri(normalized);

                var links = doc.DocumentNode //фильтруем всё ниже, сначала мы выбираем ноды <a href...>, а потом уже удаляем из полученного списка всю гадость
                    .SelectNodes("//a[@href]")
                    ?.Select(a => a.GetAttributeValue("href", "").Trim())
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Where(h => !h.StartsWith("#"))
                    .Where(h => !h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    .Where(h => !h.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    .Where(h => !h.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                    .Select(h => Uri.TryCreate(baseUri, h, out var abs) ? abs : null)
                    .Where(u => u!.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)) //только хост
                    .Where(u => string.IsNullOrEmpty(u!.Query)) //Вот это кстати очень сомнительный момент, пока пусть будет, но страницы с разными параметрами могут быть с разным содержимым
                    .Select(u => u!.AbsoluteUri)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                    ?? Array.Empty<string>();

                return links;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"__Error__ {normalized} --> {ex.Message}");
                return Array.Empty<string>();
            }
        }


        public string? Normalize(string input) //ссылки могут быть разными, нам нужно нормализовать, тобишь добавить https:// если нет, убрать порты, и т.д.
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();
            if (!input.Contains("://", StringComparison.Ordinal))
                input = "https://" + input;

            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
                return null;

            var b = new UriBuilder(uri);
            b.Host = b.Host.ToLowerInvariant();
            b.Fragment = "";

            if ((b.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && b.Port == 443) ||
                (b.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port == 80))
            {
                b.Port = -1;
            }

            if (string.IsNullOrEmpty(b.Path))
                b.Path = "/";

            if (b.Path.Length > 1 && b.Path.EndsWith("/"))
                b.Path = b.Path.TrimEnd('/');

            return b.Uri.AbsoluteUri;
        }
    }

}
