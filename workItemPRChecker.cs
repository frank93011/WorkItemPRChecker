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


public class CommitResponse
{
    public int count { get; set; }
    public List<Commit> value { get; set; }
}

public class Commit
{
    public string commitId { get; set; }
    public string comment { get; set; }
    public Author author { get; set; }
    public string url { get; set; }
}

public class Author
{
    public string name { get; set; }
    public string email { get; set; }
    public string date { get; set; }
}

public class WorkItemResponse
{
    public int count { get; set; }
    public List<WorkItem> value { get; set; }
}

public class WorkItem
{
    public string id { get; set; }
    public string url { get; set; }
}

public class WorkItemRelationResponse
{
    public List<dynamic> relations { get; set; }
}

public static class WorkItemCommitDifferenceFunction
{
    [FunctionName("WorkItemCommitDifferenceFunction")]
    public static async Task<object> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req, TraceWriter log)
    {
        string PAT = System.Environment.GetEnvironmentVariable("PAT", EnvironmentVariableTarget.Process);

        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        dynamic data = JsonConvert.DeserializeObject(requestBody);
        // log.Info("Data Received: " + data.ToString());

        string eventType = req.Headers["X-GitHub-Event"];
        // if (eventType != "pull_request")
        // {
        //     return new OkObjectResult("Not a pull request event.");
        // }

        string repositoryName = data.resource.repository.name;
        string pullRequestUrl = data.resource.url;
        string pullRequestId = data.resource.pullRequestId;
        string branchName = data.resource.sourceRefName;
        string repositoryUrl = data.resource.repository.url;
        string repoId = data.resource.repository.id;
        string projectId = data.resource.repository.project.id;
        string organization = repositoryUrl.Split('/')[3];

        // var commits = await GetCommitsFromPR(org, , PAT);
        // log.Info("commits Received: " + commits.ToString());

        var workItems = await GetWorkItemsFromPR(pullRequestUrl, PAT);
        log.Info("workItems Received: " + workItems.ToString());

        var prIds = await GetPrIdsFromWorkItems(workItems, organization, projectId, PAT);
        log.Info("PRs Received: " + prIds.ToString());

        HashSet<string> relatedCommits = new HashSet<string>();
        string responseMessage = "";
        foreach (var prId in prIds)
        {
            var commits = await GetCommitsFromPR(organization, projectId, repoId, prId, PAT);
            commits.ForEach(commit =>
            {
                relatedCommits.Add(commit.commitId);
                responseMessage += commit.commitId + '\n';
            });
        }

        return new OkObjectResult($"{responseMessage}");
    }


    private static async Task<string> GetResponseStringFromClient(string url, string accessToken)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                ASCIIEncoding.ASCII.GetBytes(
                string.Format("{0}:{1}", "", accessToken))));

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();
        return responseBody;
    }

    private static async Task<List<Commit>> GetCommitsFromPR(string org, string projectId, string repoId, string prId, string accessToken)
    {
        string targetUrl = $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/pullRequests/{prId}/commits?api-version=7.0";
        string responseBody = await GetResponseStringFromClient(targetUrl, accessToken);
        var commitResponse = JsonConvert.DeserializeObject<CommitResponse>(responseBody);

        return commitResponse.value;
    }

    private static async Task<List<WorkItem>> GetWorkItemsFromPR(string pullRequestUrl, string accessToken)
    {
        string targetUrl = $"{pullRequestUrl}/workitems?api-version=7.0";
        string responseBody = await GetResponseStringFromClient(targetUrl, accessToken);
        var workItems = JsonConvert.DeserializeObject<WorkItemResponse>(responseBody);
        return workItems.value;
    }

    private static async Task<List<string>> GetPrIdsFromWorkItems(List<WorkItem> workItems, string org, string projectId, string accessToken)
    {
        List<string> pullRequestIds = new List<string>();
        foreach (WorkItem item in workItems)
        {
            string targetUrl = $"https://dev.azure.com/{org}/{projectId}/_apis/wit/workitems/{item.id}?$expand=relations";
            string responseBody = await GetResponseStringFromClient(targetUrl, accessToken);
            dynamic prs = JsonConvert.DeserializeObject(responseBody);
            foreach (var pr in prs.relations)
            {
                if (pr.attributes.name != "Pull Request") continue;
                string url = pr.url;
                string id = url.Split("%2F").Last();
                pullRequestIds.Add(id);
            }
        }

        return pullRequestIds;
    }
}