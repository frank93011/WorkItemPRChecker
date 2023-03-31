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
using System.Net;

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
        try
        {
            string PAT = System.Environment.GetEnvironmentVariable("PAT", EnvironmentVariableTarget.Process);

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            string eventType = req.Headers["X-GitHub-Event"];
            // if (eventType != "pull_request")
            // {
            //     return new OkObjectResult("Not a pull request event.");
            // }

            string repositoryName = data.resource.repository.name;
            string pullRequestUrl = data.resource.url;
            string currentPrId = data.resource.pullRequestId;
            string sourceRefName = data.resource.sourceRefName;
            string repositoryUrl = data.resource.repository.url;
            string repoId = data.resource.repository.id;
            string projectId = data.resource.repository.project.id;
            string organization = repositoryUrl.Split('/')[3];
            string currentBranch = sourceRefName.Split('/')[2];

            var workItems = await GetWorkItemsFromPR(pullRequestUrl, PAT);
            log.Info("workItems Received: " + workItems.ToString());

            var prIds = await GetPrIdsFromWorkItems(workItems, organization, projectId, PAT);
            log.Info("PRs Received: " + prIds.ToString());

            List<string> relatedCommits = await GetRelatedCommitsFromPRs(prIds, currentPrId, organization, projectId, repoId, PAT);

            string[] targetBranchs = { "main", "SIT", "PROD" };
            string responseMessage = "";
            bool hasDiff = false;
            foreach (string targetBranch in targetBranchs)
            {
                var res = await CompareCommitsDiffWithTargetBranch(relatedCommits, organization, projectId, repoId, targetBranch, currentBranch, PAT);
                hasDiff |= res.Item1;
                responseMessage += res.Item2;
                responseMessage += "--------------\n";
            }

            log.Info($"{responseMessage}");

            PostStatusOnPullRequest(organization, projectId, repoId, currentPrId, ComputeStatus(hasDiff, responseMessage), PAT);

            return new OkObjectResult($"{responseMessage}");
        }
        catch (Exception ex)
        {
            log.Error(ex.ToString());
            return new ObjectResult(HttpStatusCode.InternalServerError + ex.ToString());
        }
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

    private static async Task<List<string>> GetRelatedCommitsFromPRs(List<string> prIds, string currentPrId, string org, string projectId, string repoId, string accessToken)
    {
        List<string> relatedCommits = new List<string>();
        foreach (var prId in prIds)
        {
            if (prId == currentPrId) continue;
            var commits = await GetCommitsFromPR(org, projectId, repoId, prId, accessToken);
            commits.ForEach(commit =>
            {
                relatedCommits.Add(commit.commitId);
            });
        }
        return relatedCommits;
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

    private static async Task<Tuple<bool, string>> CompareCommitsDiffWithTargetBranch(List<string> currentCommits, string org, string projectId, string repoId, string targetBranch, string currentBranch, string accessToken)
    {
        HashSet<string> branchTotalCommits = new HashSet<string>();
        string commitDiffMessege = $"[{targetBranch}] branch missing the following commits from linked workItems:\n";
        bool hasDiff = false;
        string targetUrl = $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/commits?searchCriteria.itemVersion.version={targetBranch}&api-version=6.0";
        string responseBody = await GetResponseStringFromClient(targetUrl, accessToken);
        CommitResponse rawCommits = JsonConvert.DeserializeObject<CommitResponse>(responseBody);
        rawCommits.value.ForEach(commit => { branchTotalCommits.Add(commit.commitId); });
        foreach (string commitId in currentCommits)
        {
            if (branchTotalCommits.Contains(commitId)) continue;
            hasDiff = true;
            commitDiffMessege += commitId + "\n";
        }
        string message = hasDiff ? commitDiffMessege : $"[{targetBranch}] branch has no linked workItem related commits difference.\n";
        return new Tuple<bool, string>(hasDiff, message);
    }

    private static string ComputeStatus(bool hasDiff, string responseMessage)
    {
        string state = !hasDiff ? "succeeded" : "pending";

        return JsonConvert.SerializeObject(
            new
            {
                State = state,
                Description = responseMessage,
                TargetUrl = "https://visualstudio.microsoft.com",

                Context = new
                {
                    Name = "PullRequest-workItemPRChecker",
                    Genre = "pr-azure-function-ci"
                }
            });
    }

    private static void PostStatusOnPullRequest(string org, string projectId, string repoId, string currentPrId, string status, string accessToken)
    {
        string targetUrl = $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/pullrequests/{currentPrId}/statuses?api-version=4.1";

        using (HttpClient client = new HttpClient())
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                    ASCIIEncoding.ASCII.GetBytes(
                    string.Format("{0}:{1}", "", accessToken))));

            var method = new HttpMethod("POST");
            var request = new HttpRequestMessage(method, targetUrl)
            {
                Content = new StringContent(status, Encoding.UTF8, "application/json")
            };

            using (HttpResponseMessage response = client.SendAsync(request).Result)
            {
                response.EnsureSuccessStatusCode();
            }
        }
    }
}