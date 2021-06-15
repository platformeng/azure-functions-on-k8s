using System;

namespace ReviewsWorkerFunctionApp.Models
{
    public class ReviewModel
    {
        public string EventId { get; internal set; }
        public string SubjectId { get; internal set; }
        public string EventType { get; internal set; }
        public Review Content { get; set; }
    }

    public class Review
    {
        public string Text { get; set; }
    }
}