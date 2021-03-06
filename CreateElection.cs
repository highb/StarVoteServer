using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Google;
using StarVoteServer.GoogleFunctions;
using System.IO;

namespace StarVoteServer
{
    public static class CreateElection
    {
        [FunctionName(nameof(Initialize))]
        public static async Task<IActionResult> Initialize(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "Initialize/{docId}")] HttpRequest req, string docId, ILogger log)
        {
            using var service = new GoogleService(docId);
            string body = await new StreamReader(req.Body).ReadToEndAsync().ConfigureAwait(false);
            // First, validate that we can access the document.
            try
            {
                var info = await service.GetSheetInfo().ConfigureAwait(false);
                if (!info.IsNewDocument())
                {
                    return new BadRequestObjectResult(@"409: CONFLICT
To avoid overwriting data, only a brand new Google Sheet document may be initialized.
Please follow these steps:
1. Create a new Google Sheets document
2. Change the title of the document to the name of your election
3. Share the document with service@starvote.iam.gserviceaccount.com granting Editor permissions
4. Retry the initialize command using the documentId of your new Google Sheets file
")
                    {
                        StatusCode = 409
                    };
                }

                var election = string.IsNullOrWhiteSpace(body) ? Election.DefaultValue() : JsonConvert.DeserializeObject<Election>(body);

                var result = await service.Initialize(election).ConfigureAwait(false);
                //TODO: What is the most useful return value?
                return new OkObjectResult(result);
            }
            catch (GoogleApiException ex)
            {
                if (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new NotFoundObjectResult($@"404: NOT FOUND from Google API
Could not find a Google Sheets document with documentId: {docId}");
                }
                if (ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return new ObjectResult($@"403: NOT AUTHORIZED from Google API
This service has not been authorized to access documentId: {docId}
Please share your Google Sheets document with service@starvote.iam.gserviceaccount.com and try again
Note that you must grant Editor access for the service to update your document")
                    {
                        StatusCode = 403
                    };
                }
                return new BadRequestObjectResult(ex);
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.GetType().AssemblyQualifiedName);
                log.LogInformation(ex.ToString());
                return new BadRequestObjectResult(ex.ToString());
            }
        }
    }
}
