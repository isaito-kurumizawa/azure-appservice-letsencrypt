﻿using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using ACMESharp.Protocol;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AzureLetsEncrypt
{
    public static class AddCertificate
    {
        [FunctionName("AddCertificate")]
        public static async Task RunOrchestrator([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            var request = context.GetInput<AddCertificateRequest>();

            var site = await context.CallActivityAsync<Site>(nameof(SharedFunctions.GetSite), (request.ResourceGroupName, request.SiteName));

            if (site == null)
            {
                log.LogInformation($"{request.SiteName} is not found");
                return;
            }

            var hostNameSslState = site.HostNameSslStates.FirstOrDefault(x => x.Name == request.Domain);

            if (hostNameSslState == null)
            {
                log.LogInformation($"{request.Domain} is not found");
                return;
            }

            await context.CallActivityAsync(nameof(SharedFunctions.UpdateSettings), site);

            // 新しく ACME Order を作成する
            var orderDetails = await context.CallActivityAsync<OrderDetails>(nameof(SharedFunctions.Order), hostNameSslState.Name);

            // 複数の Authorizations には未対応
            var authzUrl = orderDetails.Payload.Authorizations.First();

            // ACME Challenge のために Kudu API でファイルを作成
            await context.CallActivityAsync(nameof(SharedFunctions.Authorization), (site, authzUrl));
            
            if (!await context.CallActivityAsync<bool>(nameof(SharedFunctions.WaitChallenge), orderDetails))
            {
                log.LogError($"Cannot generate certificate: {hostNameSslState.Name}");
                return;
            }

            var (thumbprint, pfxBlob) = await context.CallActivityAsync<(string, byte[])>(nameof(SharedFunctions.FinalizeOrder), (hostNameSslState, orderDetails));

            await context.CallActivityAsync(nameof(SharedFunctions.UpdateCertificate), (site, $"{hostNameSslState.Name}-{thumbprint}", pfxBlob));

            hostNameSslState.Thumbprint = thumbprint;
            hostNameSslState.SslState = SslState.SniEnabled;
            hostNameSslState.ToUpdate = true;

            await context.CallActivityAsync(nameof(SharedFunctions.UpdateSiteBinding), site);
        }

        [FunctionName("AddCertificate_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestMessage req,
            [OrchestrationClient] DurableOrchestrationClient starter,
            ILogger log)
        {
            var request = await req.Content.ReadAsAsync<AddCertificateRequest>();

            if (string.IsNullOrEmpty(request.ResourceGroupName))
            {
                return req.CreateErrorResponse(System.Net.HttpStatusCode.BadRequest, $"{nameof(request.ResourceGroupName)} is empty.");
            }

            if (string.IsNullOrEmpty(request.SiteName))
            {
                return req.CreateErrorResponse(System.Net.HttpStatusCode.BadRequest, $"{nameof(request.SiteName)} is empty.");
            }

            if (string.IsNullOrEmpty(request.Domain))
            {
                return req.CreateErrorResponse(System.Net.HttpStatusCode.BadRequest, $"{nameof(request.Domain)} is empty.");
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync("AddCertificate", request);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }

    public class AddCertificateRequest
    {
        public string ResourceGroupName { get; set; }
        public string SiteName { get; set; }
        public string Domain { get; set; }
    }
}