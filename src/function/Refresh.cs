using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using Microsoft.FeatureManagement;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using System.Linq;
using Microsoft.Extensions.Configuration;
using System;
using DGC.Models;
using Microsoft.Extensions.Primitives;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Extensions;

namespace DGC.Function
{
    public class Refresh
    {

        private const string AddedDelayConfigKey = "MockAPI:AddedDelay";

        private const string ForceStatusCodeFeature = "ForceStatusCode";

        private const string ForceStatusCodeKey = "MockAPI:ForceStatusCode";

        private const string FailureRateKey = "MockAPI:FailureRate";

        private const string CertificateThumbprintEnvVariable = "DGC_LOAD_TEST_CLIENT_KEY";

        private readonly IConfiguration _configuration;

        private readonly IConfigurationRefresher _configurationRefresher;

        private readonly IFeatureManagerSnapshot _featureManagerSnapshot;

        public Refresh(IFeatureManagerSnapshot featureManagerSnapshot,
                       IConfiguration configuration,
                       IConfigurationRefresherProvider refresherProvider)
        {
            _configuration = configuration;
            _featureManagerSnapshot = featureManagerSnapshot;
            _configurationRefresher = refresherProvider.Refreshers.First();
        }

        [FunctionName("Refresh")]
        [OpenApiOperation(operationId: "managePreviousCertificates")]
        [OpenApiSecurity("function_key", SecuritySchemeType.ApiKey, Name = "code",
                         In = OpenApiSecurityLocationType.Query)]
        [OpenApiParameter(name: "cfSHA256", Required = true, Type = typeof(string),
                          Description = "The **fiscal_code** parameter")]
        [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "text/plain", bodyType: typeof(string),
                                 Description = "The OK response")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/certificate/citizen/io/refresh")]
            HttpRequest req,
            ILogger log,
            ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed Refresh/managePreviousCertificates/Run");

            var errorMessage = await Init(req, log);
            if (!errorMessage.IsNullOrWhiteSpace()) return new BadRequestObjectResult(errorMessage);

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            string fiscal_code = data?.fiscal_code;

            var path = System.IO.Path.Combine(context.FunctionDirectory, "..\\refresh_payloads.json");
            var payloadsJson = File.ReadAllText(path);
            var payloads = JsonConvert.DeserializeObject<RefreshPayloads>(payloadsJson);
            var random = new Random(DateTime.Now.Second);
            var responseMessage = payloads.PayloadList[random.Next(0, payloads.PayloadList.Count - 1)];

            var response = new ObjectResult(responseMessage);
            if (await _featureManagerSnapshot.IsEnabledAsync(ForceStatusCodeFeature) &&
                int.TryParse(_configuration[ForceStatusCodeKey], out int statusCode))
            {
                response.StatusCode = statusCode;
            }
            else
            {
                response.StatusCode = 200;
            }

            return response;
        }

        private async Task<string> Init(HttpRequest req,
                                        ILogger log)
        {
            if (!req.Headers.TryGetValue("X-ARR-ClientCert", out StringValues cert))
                return "A valid client certificate is not found";

            //Mutual auth 
            byte[] clientCertBytes = Convert.FromBase64String(cert[0]);
            X509Certificate2 clientCert = new X509Certificate2(clientCertBytes);

            // Validate Thumbprint
            if (!clientCert.Thumbprint.Equals(Environment.GetEnvironmentVariable(CertificateThumbprintEnvVariable),
                                              StringComparison.InvariantCultureIgnoreCase))
            {
                return "A valid client certificate is not used";
            }

            // Validate NotBefore and NotAfter
            if (DateTime.Compare(DateTime.UtcNow, clientCert.NotBefore) < 0
                || DateTime.Compare(DateTime.UtcNow, clientCert.NotAfter) > 0)
            {
                return "client certificate not in alllowed time interval";
            }

            // Add further validation of certificate if required.

            // Signal to refresh the configuration if the registered key(s) is modified.
            // This will be a no-op if the cache expiration time window is not reached.
            // The configuration is refreshed asynchronously without blocking the execution of the current function.
            await _configurationRefresher.TryRefreshAsync();

            if (await _featureManagerSnapshot.IsEnabledAsync("Broken"))
            {
                var r = new Random(DateTime.Now.Second).Next(1, 99);

                if (r <= Convert.ToInt32(_configuration[FailureRateKey]))
                {
                    throw new InvalidOperationException($"Broken feature flag is ON.");
                }
            }

            if (int.TryParse(_configuration[AddedDelayConfigKey], out int addedDelay) && addedDelay > 0)
            {
                log.LogInformation($"{AddedDelayConfigKey}: {addedDelay}");
                await Task.Delay(addedDelay);
            }

            return null;

        }

    }
}