﻿namespace QuoLike.Server.Helpers
{
    public class QueryObject
    {
        public bool? isFavorite { get; set; }
        public bool? isArchived { get; set; }
        public int Page { get; set; } = 1;
        public int Limit { get; set; } = 6;
    }
}
