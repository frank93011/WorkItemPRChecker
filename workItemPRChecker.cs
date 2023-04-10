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
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

public class CommitResponse
{
    public int count { get; set; }
    public List<Commit> value { get; set; }
}

public class Commit
{
    public string commitId { get; set; }
    public string comment { get; set; }
    public string url { get; set; }
    public Author author { get; set; }
}

public class Author
{
    public string name { get; set; }
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

public class Comment
{
    public int parentCommentId { get; set; }
    public string content { get; set; }
    public int contentType { get; set; }
}

public static class WorkItemCommitDifferenceFunction
{
    [FunctionName("WorkItemCommitDifferenceFunction")]
    public static async Task<object> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req, ILogger log)
    {
        try
        {
            // string eventType = req.Headers["X-GitHub-Event"];
            // if (eventType != "pull_request")
            // {
            //     return new BadRequestObjectResult("Not a pull request event.");
            // }
            string PAT = System.Environment.GetEnvironmentVariable("PAT", EnvironmentVariableTarget.Process);
            string[] targetBranchs = System.Environment.GetEnvironmentVariable("TARGET_BRANCHES", EnvironmentVariableTarget.Process).Split(',');
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            string repositoryName = data.resource.repository.name;
            string pullRequestUrl = data.resource.url;
            string currentPrId = data.resource.pullRequestId;
            string sourceRefName = data.resource.sourceRefName;
            string repositoryUrl = data.resource.repository.url;
            string repoId = data.resource.repository.id;
            string projectId = data.resource.repository.project.id;
            string organization = repositoryUrl.Split('/')[3];
            string currentBranch = sourceRefName.Split('/')[2];
            string responseMessage = "";
            bool hasDiff = false;

            var workItems = await GetWorkItemsFromPR(pullRequestUrl, PAT);
            var pullRequestIds = await GetPullRequestIdsFromWorkItems(workItems, organization, projectId, PAT);
            var relatedCommitsResponse = await GetRelatedCommitsAndEarliestDateFromPRs(pullRequestIds, currentPrId, organization, projectId, repoId, PAT);
            List<Commit> relatedCommits = relatedCommitsResponse.Item1;
            string earliestCommitDate = relatedCommitsResponse.Item2;

            foreach (string targetBranch in targetBranchs)
            {
                var res = await CompareCommitsDiffWithTargetBranch(relatedCommits, earliestCommitDate, organization, projectId, repoId, targetBranch, currentBranch, PAT);
                hasDiff |= res.Item1;
                responseMessage += res.Item2;
                responseMessage += "\n";
            }

            log.LogInformation($"{responseMessage}");

            PostStatusOnPullRequest(organization, projectId, repoId, currentPrId, hasDiff, PAT);
            if (hasDiff) PostCommentOnPullRequest(organization, projectId, repoId, currentPrId, responseMessage, PAT);

            return new OkObjectResult($"{responseMessage}");
        }
        catch (Exception ex)
        {
            log.LogError(ex.ToString());
            return new BadRequestObjectResult(ex.ToString());
        }
    }


    private static async Task<string> GetResponseFromClient(string url, string accessToken)
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

    private static void PostToClient(string targetUrl, string message, string accessToken)
    {
        HttpClient client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                ASCIIEncoding.ASCII.GetBytes(
                string.Format("{0}:{1}", "", accessToken))));

        var method = new HttpMethod("POST");
        var request = new HttpRequestMessage(method, targetUrl)
        {
            Content = new StringContent(message, Encoding.UTF8, "application/json")
        };

        using (HttpResponseMessage response = client.SendAsync(request).Result)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private static async Task<string> PostToClientWithResponse(string targetUrl, string message, string accessToken)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                ASCIIEncoding.ASCII.GetBytes(
                string.Format("{0}:{1}", "", accessToken))));

        var method = new HttpMethod("POST");
        var request = new HttpRequestMessage(method, targetUrl)
        {
            Content = new StringContent(message, Encoding.UTF8, "application/json")
        };

        var response = client.SendAsync(request).Result;
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();
        return responseBody;
    }

    private static async Task<List<Commit>> GetCommitsFromPR(string org, string projectId, string repoId, string prId, string accessToken)
    {
        string targetUrl = $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/pullRequests/{prId}/commits?api-version=7.0";
        string responseBody = await GetResponseFromClient(targetUrl, accessToken);
        var commitResponse = JsonConvert.DeserializeObject<CommitResponse>(responseBody);

        return commitResponse.value;
    }

    private static async Task<Tuple<List<Commit>, string>> GetRelatedCommitsAndEarliestDateFromPRs(List<string> pullRequestIds, string currentPrId, string org, string projectId, string repoId, string accessToken)
    {
        HashSet<Commit> relatedCommits = new HashSet<Commit>();
        DateTime earliestCommitDate = DateTime.MaxValue;
        foreach (var prId in pullRequestIds)
        {
            if (prId == currentPrId) continue;
            var commits = await GetCommitsFromPR(org, projectId, repoId, prId, accessToken);
            commits.ForEach(commit =>
            {
                relatedCommits.Add(commit);
                DateTime commitTime = Convert.ToDateTime(commit.author.date);
                earliestCommitDate = commitTime < earliestCommitDate ? commitTime : earliestCommitDate;
            });
        }
        return new Tuple<List<Commit>, string>(relatedCommits.ToList(), earliestCommitDate.Date.ToString("s"));
    }

    private static async Task<List<WorkItem>> GetWorkItemsFromPR(string pullRequestUrl, string accessToken)
    {
        string targetUrl = $"{pullRequestUrl}/workitems?api-version=7.0";
        string responseBody = await GetResponseFromClient(targetUrl, accessToken);
        var workItems = JsonConvert.DeserializeObject<WorkItemResponse>(responseBody);
        return workItems.value;
    }

    private static async Task<List<string>> GetPullRequestIdsFromWorkItems(List<WorkItem> workItems, string org, string projectId, string accessToken)
    {
        List<string> pullRequestIds = new List<string>();
        foreach (WorkItem item in workItems)
        {
            string targetUrl = $"https://dev.azure.com/{org}/{projectId}/_apis/wit/workitems/{item.id}?$expand=relations&api-version=7.0";
            string responseBody = await GetResponseFromClient(targetUrl, accessToken);
            dynamic pullRequests = JsonConvert.DeserializeObject(responseBody);
            foreach (var pr in pullRequests.relations)
            {
                if (pr.attributes.name != "Pull Request") continue;
                string url = pr.url;
                string id = url.Split("%2F").Last();
                pullRequestIds.Add(id);
            }
        }

        return pullRequestIds;
    }

    private static async Task<Tuple<bool, string>> CompareCommitsDiffWithTargetBranch(List<Commit> currentCommits, string earliestCommitDate, string org, string projectId, string repoId, string targetBranch, string currentBranch, string accessToken)
    {
        string targetUrl = $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/commitsBatch?api-version=7.0";
        string noDiffMessage = $"[{targetBranch}] branch has no linked workItem related commits difference.\n";
        string commitDiffMessege = $"[{targetBranch}] branch missing the following commits from linked workItems:\n";
        bool hasDiff = false;
        HashSet<string> branchCommits = new HashSet<string>();
        string requestBody = JsonConvert.SerializeObject(new
        {
            FromDate = earliestCommitDate,
            itemVersion = new
            {
                versionType = "branch",
                version = targetBranch,
            }
        });

        string responseBody = await PostToClientWithResponse(targetUrl, requestBody, accessToken);
        var commitResponse = JsonConvert.DeserializeObject<CommitResponse>(responseBody);

        commitResponse.value.ForEach(commit => { branchCommits.Add(commit.comment); });
        foreach (Commit commit in currentCommits)
        {
            if (branchCommits.Contains(commit.comment)) continue;
            hasDiff = true;
            commitDiffMessege += $"- [{commit.comment}]({commit.url}) \n";
        }
        string responseMessage = hasDiff ? commitDiffMessege : noDiffMessage;
        return new Tuple<bool, string>(hasDiff, responseMessage);
    }

    private static void PostStatusOnPullRequest(string org, string projectId, string repoId, string currentPrId, bool hasDiff, string accessToken)
    {
        string targetUrl = $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/pullrequests/{currentPrId}/statuses?api-version=7.0";

        string state = !hasDiff ? "succeeded" : "failed";
        string description = !hasDiff ? "No commits dependency lost detected" : "Detect commits dependency lost";
        string status = JsonConvert.SerializeObject(
            new
            {
                State = state,
                Description = description,
                TargetUrl = "https://visualstudio.microsoft.com",

                Context = new
                {
                    Name = "PullRequest-workItemPRChecker",
                    Genre = "pr-azure-function-ci"
                }
            });

        PostToClient(targetUrl, status, accessToken);
        return;
    }

    private static void PostCommentOnPullRequest(string org, string projectId, string repoId, string currentPrId, string responseMessage, string accessToken)
    {
        string targetUrl = $"https://dev.azure.com/{org}/{projectId}/_apis/git/repositories/{repoId}/pullrequests/{currentPrId}/threads?api-version=7.0";
        List<Comment> comments = new List<Comment>();
        comments.Add(new Comment { parentCommentId = 0, content = responseMessage, contentType = 1 });
        string requestBody = JsonConvert.SerializeObject(
            new
            {
                Comments = comments,
                status = 1
            });

        PostToClient(targetUrl, requestBody, accessToken);
        return;
    }
}