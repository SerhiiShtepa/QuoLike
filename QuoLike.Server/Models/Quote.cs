﻿namespace QuoLike.Server.Models
{
    public class Quote
    {
        public string QuoteId { get; set; }
        public string ExternalId { get; set; }
        public bool? IsFavorite { get; set; }
        public bool? IsArchived { get; set; }
    }
}
