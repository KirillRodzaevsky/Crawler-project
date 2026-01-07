using Crawler_project.Models;
using Microsoft.AspNetCore.Mvc;
using static Crawler_project.Models.DTO;

namespace Crawler_project.Controllers
{
    
    /// <summary>
    /// Ќу это база, € б сказал основа основ.
    ///  ороче немного переделанный базовый шаблон контроллера, который идЄт базово при создании проекта с ASP.NET
    /// </summary>
    [ApiController]
    [Route("api/crawl")] 
    public sealed class CrawlController : ControllerBase
    {
        private readonly JobStore _store;

        public CrawlController(JobStore store) => _store = store;

        [HttpPost]
        public ActionResult<CrawlStartResponse> Start([FromBody] CrawlStartRequest req)
        {
            var job = _store.CreateAndStart(req);

            var resp = new CrawlStartResponse(
                job.JobId,
                StatusUrl: $"/api/crawl/{job.JobId}",
                UrlsUrl: $"/api/crawl/{job.JobId}/urls"
            );

            return Accepted(resp);
        }

        [HttpGet("info")]
        public string Get()
        {
            return logo.Show();
        }

        [HttpGet("{jobId:guid}")]
        public ActionResult<CrawlStatusResponse> Status(Guid jobId)
        {
            var job = _store.Get(jobId);
            if (job is null) return NotFound();

            return new CrawlStatusResponse(
                job.JobId,
                job.State.ToString(),
                job.StartUrl,
                job.MaxPages,
                job.Workers,
                Visited: job.VisitedCount,
                Discovered: job.Discovered.Count,
                job.StartedAt,
                job.FinishedAt,
                job.Error
            );
        }

        [HttpGet("{jobId:guid}/urls")]
        public ActionResult<PagedUrlsResponse> Urls(Guid jobId, [FromQuery] int offset = 0, [FromQuery] int limit = 100)
        {
            var urls = _store.GetUrls(jobId, offset, limit, out var total);
            if (total == 0 && _store.Get(jobId) is null) return NotFound();

            return new PagedUrlsResponse(total, offset, limit, urls);
        }

        [HttpPost("{jobId:guid}/cancel")]
        public IActionResult Cancel(Guid jobId)
        {
            if (_store.Get(jobId) is null) return NotFound();
            _store.Cancel(jobId);
            return Accepted();
        }
    }
}
