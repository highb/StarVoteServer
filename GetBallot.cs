﻿using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Google;
using StarVoteServer.GoogleFunctions;
using System.IO;
using System.Collections.Generic;

namespace StarVoteServer
{
    public static class GetBallot
    {
        [FunctionName(nameof(Ballot))]
        public static async Task<IActionResult> Ballot(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "Ballot/{docId}")] HttpRequest req, string docId, ILogger log)
        {
            using var service = new GoogleService(docId);
            string body = await new StreamReader(req.Body).ReadToEndAsync().ConfigureAwait(false);
            try
            {
                var ballot = await Election.ReadBallot(service).ConfigureAwait(false);
                return new OkObjectResult(ballot);
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
