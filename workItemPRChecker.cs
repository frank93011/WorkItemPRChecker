using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;

public static class WorkItemCommitDifferenceFunction
{
    [FunctionName("WorkItemCommitDifferenceFunction")]
    public static async Task<object> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req, TraceWriter log)
    {
        string PAT = Environment.GetEnvironmentVariable("PAT", EnvironmentVariableTarget.Process);
        log.Info("TEST: " + PAT);


        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        dynamic data = JsonConvert.DeserializeObject(requestBody);
        log.Info("Data Received: " + data.ToString());

        string eventType = req.Headers["X-GitHub-Event"];
        // if (eventType != "pull_request")
        // {
        //     return new OkObjectResult("Not a pull request event.");
        // }

        string repositoryName = data.resource.repository.name;
        string pullRequestUrl = data.resource.url;
        string branchName = data.resource.sourceRefName;
        string baseBranchName = "test";

        var commits = await GetPullRequestCommits(pullRequestUrl, PAT);
        log.Info("commits Received: " + commits.ToString());

        // var workItemClient = new HttpClient();
        // var workItemRequest = new HttpRequestMessage(HttpMethod.Get, $"https://dev.azure.com/{repositoryName}/_apis/wit/workitems?ids={string.Join(',', commits.Select(c => c.id.ToString()))}&api-version=6.1-preview.3");
        // HttpResponseMessage workItemResponse = await workItemClient.SendAsync(workItemRequest);
        // string workItemResponseContent = await workItemResponse.Content.ReadAsStringAsync();
        // dynamic workItems = JsonConvert.DeserializeObject(workItemResponseContent);
        // log.Info("workItems Received: " + workItems.ToString());


        return new OkObjectResult($"There is no difference between {branchName} and {baseBranchName} in the work item commit.");
    }

    private static async Task<dynamic> GetPullRequestCommits(string pullRequestUrl, string accessToken)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                ASCIIEncoding.ASCII.GetBytes(
                string.Format("{0}:{1}", "", accessToken))));

        var response = await client.GetAsync($"{pullRequestUrl}/commits?api-version=6.0");
        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync();
        var commits = JsonConvert.DeserializeObject<dynamic>(responseBody);

        return commits;
    }
}