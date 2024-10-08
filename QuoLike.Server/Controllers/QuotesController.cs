﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using QuoLike.Server.Data;
using QuoLike.Server.Data.Repositories;
using QuoLike.Server.DTOs;
using QuoLike.Server.Helpers;
using QuoLike.Server.Mappers;
using QuoLike.Server.Models;
using QuoLike.Server.Models.Quotable;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace QuoLike.Server.Controllers
{
    [Route("api/quotes")]
    [ApiController]
    [Authorize]
    public class QuotesController : ControllerBase
    {
        private readonly ILogger<QuotesController> _logger;
        private readonly IQuoteRepository _quoteRepository;
        private readonly HttpClient _httpClient;
        private readonly UserManager<IdentityUser> _userManager;
        public QuotesController(ILogger<QuotesController> logger, IQuoteRepository quoteSelectRepository,
            HttpClient httpClient, UserManager<IdentityUser> userManager)
        {
            _logger = logger;
            _quoteRepository = quoteSelectRepository;
            _httpClient = httpClient;
            _userManager = userManager;
        }

        [HttpGet("merged")]
        public async Task<IActionResult> GetQuotableMerged([FromQuery] QueryObject queryObject)
        {
            string requestUrl = $"https://api.quotable.io/quotes?page={queryObject.Page}&limit={queryObject.Limit}";

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // works even with no SSL
            };

            HttpClient httpClient = new HttpClient(handler);

            var response = await httpClient.GetAsync(requestUrl);
            var data = await response.Content.ReadAsStringAsync();
            var quotableQuotes = JsonConvert.DeserializeObject<QuotableQuoteConnection>(data);

            // Find all quotes in database (by quotable page quotes)
            List<Quote> dbQuotes = new();

            int dbPage = 1;
            string? userId = _userManager.GetUserId(User);
            int totalDbPages = (int)Math.Ceiling(await _quoteRepository.GetTotalAsync(userId) / (double)queryObject.Limit);
            do
            {
                var quotes = await _quoteRepository.GetPaginatedAsync(new QueryObject() { Page = dbPage, Limit = 6 }, userId);

                if (quotes.Count() == 0)
                {
                    break;
                }
                else
                {
                    dbQuotes.AddRange(quotes
                        .Where(q => quotableQuotes.Results.Any(qtb => qtb._id == q._id)));
                }
                dbPage++;
            } while (dbPage <= totalDbPages);

            // Merge quotables with db quotes (left join)
            var merged = from qtb in quotableQuotes.Results
                         join q in dbQuotes on qtb._id equals q._id into gj
                         from subgroup in gj.DefaultIfEmpty()
                         select new MergedQuoteDTO()
                         {
                             Tags = qtb.Tags,
                             _id = qtb._id,
                             Content = qtb.Content,
                             Author = qtb.Author,
                             AuthorSlug = qtb.AuthorSlug,
                             Length = qtb.Length,
                             DateAdded = qtb.DateAdded,
                             DateModified = qtb.DateModified,
                             IsFavorite = subgroup is null ? false : subgroup.IsFavorite,
                             IsArchived = subgroup is null ? false : subgroup.IsArchived,
                         };
            return Ok(new MergedQuotesDTO
            {
                Page = queryObject.Page,
                Count = merged.Count(),
                TotalCount = quotableQuotes.TotalCount,
                TotalPages = (int)Math.Ceiling(quotableQuotes.TotalCount / (double)queryObject.Limit),
                Results = merged
            });
        }

        [HttpGet("all")]
        public async Task<IActionResult> GetAll([FromQuery] QueryObject queryObject)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);
            string? userId = _userManager.GetUserId(User);
            var dbQuotes = await _quoteRepository.GetPaginatedAsync(queryObject, userId);
            int totalDbQuotes = await _quoteRepository.GetTotalAsync(userId);
            int totalDbPages = (int)Math.Ceiling(totalDbQuotes / (double)queryObject.Limit);

            IEnumerable<MergedQuoteDTO> results = dbQuotes.Select(quote => quote.ToMergedQuoteDTO());
            return Ok(new MergedQuotesDTO
            {
                Page = queryObject.Page,
                Count = dbQuotes.Count(),
                TotalCount = totalDbQuotes,
                TotalPages = totalDbPages,
                Results = results
            });
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Get([FromRoute] string id)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            string? userId = _userManager.GetUserId(User);

            var q = await _quoteRepository.GetAsync(id, userId);

            if (q is null)
                return NotFound();

            return Ok(q.ToQuoteDTO());
        }

        [HttpPost("create")]
        public async Task<IActionResult> Create([FromBody] QuoteCreateDTO quote)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            string? userId = _userManager.GetUserId(User);

            var existingQuote = await _quoteRepository.GetByExternalIdAsync(quote._id, userId);
            if (existingQuote == null)
            {
                var toAdd = quote.ToQuote();
                toAdd.UserId = userId;
                existingQuote = await _quoteRepository.AddAsync(toAdd);
                return CreatedAtAction(nameof(Get), new { id = existingQuote.QuoteId }, existingQuote.ToQuoteDTO());
            }

            var updateDTO = quote.ToQuote().ToUpdateDTO();
            updateDTO.QuoteId = existingQuote.QuoteId;

            return await Update(updateDTO);
        }

        [HttpPut("edit/{id}")]
        public async Task<IActionResult> Update([FromBody] QuoteUpdateDTO quote)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            string? userId = _userManager.GetUserId(User);

            var existingQuote = await _quoteRepository.GetByExternalIdAsync(quote._id, userId);
            if (existingQuote == null)
            {
                return NotFound("Quote not found");
            }

            // delete if not favorite and not archived
            if (quote.IsFavorite == false && quote.IsArchived == false)
            {
                return await Delete(quote.QuoteId);
            }

            // update
            existingQuote.IsFavorite = quote.IsFavorite;
            existingQuote.IsArchived = quote.IsArchived;

            var q = await _quoteRepository.UpdateAsync(existingQuote);

            if (q == null)
            {
                return NotFound("Quote not found");
            }

            return Ok(q.ToQuoteDTO());
        }

        [HttpDelete("delete/{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            string? userId = _userManager.GetUserId(User);

            var q = await _quoteRepository.DeleteAsync(id, userId);

            if (q == null)
            {
                return NotFound("Quote not found");
            }

            return Ok(q.ToQuoteDTO());
        }

        [HttpGet("random")]
        public async Task<IActionResult> GetQuotableRandom()
        {
            string requestUrl = $"https://api.quotable.io/random";

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // works even with no SSL
            };

            HttpClient httpClient = new HttpClient(handler);

            var response = await httpClient.GetAsync(requestUrl);
            var data = await response.Content.ReadAsStringAsync();
            var quotableQuote = JsonConvert.DeserializeObject<QuotableQuote>(data);
            return Ok(quotableQuote);
        }
    }
}
