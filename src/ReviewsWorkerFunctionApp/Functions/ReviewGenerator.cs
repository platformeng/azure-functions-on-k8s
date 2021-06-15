using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Bogus;
using ReviewsWorkerFunctionApp.Utilities;
using ReviewsWorkerFunctionApp.Models;

namespace ReviewsWorkerFunctionApp
{
    public static class ReviewGenerator
    {
        [FunctionName(nameof(ReviewGenerator))]
        public static IActionResult Run(
            [HttpTrigger(
                AuthorizationLevel.Anonymous,
                "get",
                "post",
                Route = "review")] HttpRequest req,
            ILogger log,
            [Queue(
                queueName: QueueHelper.REVIEW_QUEUE_NAME,
                Connection = QueueHelper.REVIEW_QUEUE_CONNECTION_STRING_NAME)] out string reviewMessage)
        {
            log.LogInformation($"Generating review with {nameof(ReviewGenerator)}.");

            var faker = new Faker("en");

            var messageString = JsonConvert.SerializeObject(
                new ReviewModel
                {
                    EventId = Guid.NewGuid().ToString(),
                    SubjectId = Guid.NewGuid().ToString(),
                    EventType = "ReviewSubmitted",
                    Content = new Review {
                        Text = faker.Rant.Review()
                    }
                });

            reviewMessage = messageString;

            log.LogInformation($"Sending review: {messageString}");

            return new OkObjectResult(messageString);
        }
    }
}
