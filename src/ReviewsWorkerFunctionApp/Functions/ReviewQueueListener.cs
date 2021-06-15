using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using ReviewsWorkerFunctionApp.Utilities;

namespace ReviewsWorkerFunctionApp
{
    public static class ReviewQueueListener
    {
        [FunctionName(nameof(ReviewQueueListener))]
        public static async Task Run(
            [QueueTrigger(
                queueName: QueueHelper.REVIEW_QUEUE_NAME,
                Connection = QueueHelper.REVIEW_QUEUE_CONNECTION_STRING_NAME)] string reviewString,
                ILogger log)
        {
            log.LogInformation($"Started processing at {DateTime.UtcNow:o}. in: {nameof(ReviewQueueListener)}");

            // Wait for 15 seconds to allow messages to build up in the queue.
            await Task.Delay(15 * 1000);

            log.LogInformation($"Finished processing at {DateTime.UtcNow:o}. in: {nameof(ReviewQueueListener)} with:{Environment.NewLine}{reviewString}");
        }
    }
}
