using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Amazon.S3;
using MailKit.Net.Smtp;
using MimeKit;
using System.Text;
using System.Threading;

class Program
{
    static async Task Main(string[] args)
    {
        args = "--github-token=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 --download-releases".Split(" ");

        var config = new BackupConfig();
        config.ParseArguments(args);
        config.ValidateArguments();
        config.ConfigureHttpClient();

        var backup = new GitHubBackup(config);

        try
        {
            await backup.ExecuteBackup();
        }
        catch (Exception ex)
        {
            backup.LogError(ex);
        }
    }
}

class GitHubBackup
{
    private readonly BackupConfig _config;
    private static readonly HttpClient httpClient = new HttpClient();

    public GitHubBackup(BackupConfig config)
    {
        _config = config;
    }

    public async Task ExecuteBackup()
    {
        string userName = await FetchGitHubUserName();
        var repositories = await FetchRepositories(userName);

        var backupTasks = new ConcurrentBag<Task>();

        foreach (var repo in repositories)
        {
            string repoName = repo["name"].ToString();
            EnqueueBackupTasks(userName, repoName, backupTasks);
        }

        await Task.WhenAll(backupTasks);

        if (_config.CompressBackup)
        {
            CompressBackupFiles();
        }

        if (_config.UploadToS3)
        {
            await UploadToS3Async();
        }

        Log("Backup completed successfully.");
        NotifyCompletion("GitHub Backup Completed", "The GitHub backup completed successfully.");
        if (_config.NotifyViaSlack)
        {
            NotifySlack("The GitHub backup completed successfully.");
        }
    }

    private void EnqueueBackupTasks(string userName, string repoName, ConcurrentBag<Task> backupTasks)
    {
        backupTasks.Add(BackupRepository(repoName));

        if (_config.DownloadIssues)
        {
            backupTasks.Add(BackupData(userName, repoName, "issues", "issues.json"));
        }
        if (_config.DownloadPulls)
        {
            backupTasks.Add(BackupData(userName, repoName, "pulls", "pull_requests.json"));
        }
        if (_config.DownloadWiki)
        {
            backupTasks.Add(BackupWiki(userName, repoName));
        }
        if (_config.DownloadReleases)
        {
            backupTasks.Add(BackupReleaseAssets(userName, repoName));
        }
        if (_config.DownloadMetadata)
        {
            backupTasks.Add(BackupRepositoryMetadata(userName, repoName));
        }
        if (_config.DownloadProjects)
        {
            backupTasks.Add(BackupProjectBoards(userName, repoName));
        }
    }

    private async Task<string> FetchGitHubUserName()
    {
        string response = await SendHttpRequestAsync($"{BackupConfig.GitHubApiUrl}/user");
        var json = JObject.Parse(response);
        return json["login"].ToString();
    }

    private async Task<JArray> FetchRepositories(string userName)
    {
        string response = await SendHttpRequestAsync($"{BackupConfig.GitHubApiUrl}/users/{userName}/repos");
        return JArray.Parse(response);
    }

    private async Task BackupRepository(string repoName)
    {
        string cloneUrl = $"https://github.com/{repoName}.git";
        string backupPath = Path.Combine(_config.BackupDirectory, repoName);

        Directory.CreateDirectory(backupPath);

        string gitCloneCommand = $"git clone --mirror {cloneUrl} {backupPath}";
        await ExecuteShellCommand(gitCloneCommand);

        Log($"Repository {repoName} backed up.");
    }

    private async Task BackupData(string userName, string repoName, string dataType, string fileName)
    {
        string url = $"{BackupConfig.GitHubApiUrl}/repos/{userName}/{repoName}/{dataType}";
        await BackupPaginatedData(url, Path.Combine(repoName, fileName));

        Log($"{dataType} for repository {repoName} backed up.");
    }

    private async Task BackupWiki(string userName, string repoName)
    {
        string wikiUrl = $"https://github.com/{userName}/{repoName}.wiki.git";
        string backupPath = Path.Combine(_config.BackupDirectory, repoName, "wiki");

        Directory.CreateDirectory(backupPath);

        string gitCloneCommand = $"git clone {wikiUrl} {backupPath}";
        await ExecuteShellCommand(gitCloneCommand);

        Log($"Wiki for repository {repoName} backed up.");
    }

    private async Task BackupReleaseAssets(string userName, string repoName)
    {
        string releasesUrl = $"{BackupConfig.GitHubApiUrl}/repos/{userName}/{repoName}/releases";
        var releases = JArray.Parse(await SendHttpRequestAsync(releasesUrl));

        var downloadTasks = releases
            .SelectMany(release => release["assets"])
            .Select(asset => DownloadFileAsync(asset["browser_download_url"].ToString(), Path.Combine(_config.BackupDirectory, repoName, "releases", asset["name"].ToString())))
            .ToArray();

        await Task.WhenAll(downloadTasks);

        Log($"Release assets for repository {repoName} backed up.");
    }

    private async Task BackupRepositoryMetadata(string userName, string repoName)
    {
        string repoUrl = $"{BackupConfig.GitHubApiUrl}/repos/{userName}/{repoName}";
        string response = await SendHttpRequestAsync(repoUrl);
        string backupPath = Path.Combine(_config.BackupDirectory, repoName, "metadata.json");

        await File.WriteAllTextAsync(backupPath, response);

        Log($"Metadata for repository {repoName} backed up.");
    }

    private async Task BackupProjectBoards(string userName, string repoName)
    {
        string projectsUrl = $"{BackupConfig.GitHubApiUrl}/repos/{userName}/{repoName}/projects";
        string response = await SendHttpRequestAsync(projectsUrl);
        string backupPath = Path.Combine(_config.BackupDirectory, repoName, "projects.json");

        await File.WriteAllTextAsync(backupPath, response);

        Log($"Project boards for repository {repoName} backed up.");
    }

    private async Task BackupPaginatedData(string url, string fileName)
    {
        var allData = new JArray();
        string backupPath = Path.Combine(_config.BackupDirectory, fileName);

        while (!string.IsNullOrEmpty(url))
        {
            var response = await httpClient.GetAsync(url);
            var data = JArray.Parse(await response.Content.ReadAsStringAsync());
            allData.Merge(data);

            url = GetNextPageUrl(response.Headers);
        }

        await File.WriteAllTextAsync(backupPath, allData.ToString());

        Log($"{fileName} backed up.");
    }

    private string GetNextPageUrl(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("Link", out var links))
        {
            var linkHeader = links.FirstOrDefault();
            var linksArray = linkHeader.Split(',');

            return linksArray
                .Select(link => link.Split(';'))
                .Where(segments => segments.Length == 2 && segments[1].Contains("rel=\"next\""))
                .Select(segments => segments[0].Trim('<', '>', ' '))
                .FirstOrDefault();
        }

        return null;
    }

    private async Task<string> SendHttpRequestAsync(string url)
    {
        using var client = _config.GetHttpClient();
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    private async Task DownloadFileAsync(string url, string path)
    {
        var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs);

        Log($"Downloaded asset from {url} to {path}");
    }

    private static Task ExecuteShellCommand(string command)
    {
        var tcs = new TaskCompletionSource<bool>();
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.EnableRaisingEvents = true;
        process.Exited += (sender, args) =>
        {
            tcs.SetResult(true);
            process.Dispose();
        };

        process.Start();
        return tcs.Task;
    }

    private void CompressBackupFiles()
    {
        string zipFilePath = Path.Combine(_config.BackupDirectory, "backup.zip");

        if (File.Exists(zipFilePath))
        {
            File.Delete(zipFilePath);
        }

        System.IO.Compression.ZipFile.CreateFromDirectory(_config.BackupDirectory, zipFilePath);

        Log("Backup directory compressed.");
    }

    private async Task UploadToS3Async()
    {
        //using var client = new AmazonS3Client(AwsAccessKeyId, AwsSecretAccessKey, Amazon.RegionEndpoint.USEast1);
        //var fileTransferUtility = new TransferUtility(client);

        //string filePath = Path.Combine(BackupDirectory, "backup.zip");
        //await fileTransferUtility.UploadAsync(filePath, S3BucketName);

        Log("Backup uploaded to S3.");
    }

    private void Log(string message)
    {
        string logPath = Path.Combine(_config.BackupDirectory, "backup.log");
        string logMessage = $"{DateTime.Now}: {message}";
        File.AppendAllText(logPath, logMessage + Environment.NewLine);
    }

    private void NotifyCompletion(string subject, string body)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GitHub Backup", _config.EmailSender));
            message.To.Add(new MailboxAddress("Recipient", _config.EmailRecipient));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            client.Connect(_config.SmtpServer, _config.SmtpPort, false);
            client.Authenticate(_config.SmtpUsername, _config.SmtpPassword);
            client.Send(message);
            client.Disconnect(true);

            Log("Notification sent.");
        }
        catch (Exception ex)
        {
            Log($"Failed to send notification: {ex.Message}");
        }
    }

    private void NotifySlack(string message)
    {
        try
        {
            var payload = new { text = message };
            var payloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            var httpContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            var response = httpClient.PostAsync(_config.SlackWebhookUrl, httpContent).Result;
            response.EnsureSuccessStatusCode();

            Log("Slack notification sent.");
        }
        catch (Exception ex)
        {
            Log($"Failed to send Slack notification: {ex.Message}");
        }
    }

    public void LogError(Exception ex)
    {
        Log($"Error occurred: {ex.Message}");
        NotifyCompletion("GitHub Backup Error", $"An error occurred during the GitHub backup: {ex.Message}");
        if (_config.NotifyViaSlack)
        {
            NotifySlack($"An error occurred during the GitHub backup: {ex.Message}");
        }
    }
}
