using System.Net.Http.Headers;

class BackupConfig
{
    public string GitHubToken { get; private set; }
    public string BackupDirectory { get; private set; } = @"C:\GitHubBackups";
    public string AwsAccessKeyId { get; private set; }
    public string AwsSecretAccessKey { get; private set; }
    public string S3BucketName { get; private set; }
    public string EmailRecipient { get; private set; }
    public string EmailSender { get; private set; }
    public string SmtpServer { get; private set; }
    public int SmtpPort { get; private set; } = 587;
    public string SmtpUsername { get; private set; }
    public string SmtpPassword { get; private set; }
    public string SlackWebhookUrl { get; private set; }
    public bool CompressBackup { get; private set; }
    public bool UploadToS3 { get; private set; }
    public bool NotifyViaSlack { get; private set; }
    public bool DownloadIssues { get; private set; }
    public bool DownloadPulls { get; private set; }
    public bool DownloadWiki { get; private set; }
    public bool DownloadReleases { get; private set; }
    public bool DownloadMetadata { get; private set; }
    public bool DownloadProjects { get; private set; }
    public static object GitHubApiUrl { get; internal set; }

    private static readonly HttpClient httpClient = new HttpClient();

    public void ParseArguments(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--github-token="))
            {
                GitHubToken = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--backup-dir="))
            {
                BackupDirectory = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--aws-access-key-id="))
            {
                AwsAccessKeyId = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--aws-secret-access-key="))
            {
                AwsSecretAccessKey = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--s3-bucket="))
            {
                S3BucketName = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--email-recipient="))
            {
                EmailRecipient = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--email-sender="))
            {
                EmailSender = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--smtp-server="))
            {
                SmtpServer = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--smtp-port="))
            {
                SmtpPort = int.Parse(arg.Split('=')[1]);
            }
            else if (arg.StartsWith("--smtp-username="))
            {
                SmtpUsername = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--smtp-password="))
            {
                SmtpPassword = arg.Split('=')[1];
            }
            else if (arg.StartsWith("--slack-webhook-url="))
            {
                SlackWebhookUrl = arg.Split('=')[1];
                NotifyViaSlack = true;
            }
            else if (arg == "--compress-backup")
            {
                CompressBackup = true;
            }
            else if (arg == "--upload-to-s3")
            {
                UploadToS3 = true;
            }
            else if (arg == "--download-issues")
            {
                DownloadIssues = true;
            }
            else if (arg == "--download-pulls")
            {
                DownloadPulls = true;
            }
            else if (arg == "--download-wiki")
            {
                DownloadWiki = true;
            }
            else if (arg == "--download-releases")
            {
                DownloadReleases = true;
            }
            else if (arg == "--download-metadata")
            {
                DownloadMetadata = true;
            }
            else if (arg == "--download-projects")
            {
                DownloadProjects = true;
            }
        }
    }

    public void ValidateArguments()
    {
        if (string.IsNullOrEmpty(GitHubToken))
        {
            throw new ArgumentException("GitHub token is required. Use --github-token=<token>");
        }

        if (!DownloadIssues && !DownloadPulls && !DownloadWiki && !DownloadReleases && !DownloadMetadata && !DownloadProjects)
        {
            throw new ArgumentException("At least one download option is required. Use --download-issues, --download-pulls, --download-wiki, --download-releases, --download-metadata, or --download-projects.");
        }

        if (UploadToS3 && (string.IsNullOrEmpty(AwsAccessKeyId) || string.IsNullOrEmpty(AwsSecretAccessKey) || string.IsNullOrEmpty(S3BucketName)))
        {
            throw new ArgumentException("AWS credentials and S3 bucket name are required for uploading to S3. Use --aws-access-key-id, --aws-secret-access-key, and --s3-bucket.");
        }
    }

    public void ConfigureHttpClient()
    {
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", GitHubToken);
    }

    public HttpClient GetHttpClient() => httpClient;
}
