using GitHubDevOpsLink.Services.Data;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GitHubDevOpsLink.Services;

public sealed class LocalRepositoryService : ILocalRepositoryService
{
    private readonly ILogger<LocalRepositoryService> _logger;
    private readonly IDatabaseService _databaseService;
    private readonly IGitHubService _githubService;

    public LocalRepositoryService(ILogger<LocalRepositoryService> logger, IDatabaseService databaseService, IGitHubService githubService)
    {
        _logger = logger;
        _databaseService = databaseService;
        _githubService = githubService;
    }

    public async Task ScanAndLinkRepositoriesAsync(string workFolderPath, string owner)
    {
        _logger.LogInformation("Starting repository scan in folder: {WorkFolderPath} for owner: {Owner}", workFolderPath, owner);

        if (!Directory.Exists(workFolderPath))
        {
            _logger.LogWarning("Work folder path does not exist: {WorkFolderPath}", workFolderPath);
            return;
        }

        try
        {
            // First, cleanup invalid linked repositories
            _logger.LogDebug("Running cleanup of invalid linked repositories before scanning");
            var cleanedCount = await CleanupInvalidLinkedRepositoriesAsync(owner);
            _logger.LogInformation("Cleanup completed: {CleanedCount} invalid links removed", cleanedCount);

            // Get all GitHub repositories from database
            var repositories = await _databaseService.GetGitHubRepositoriesAsync(owner);
            _logger.LogInformation("Found {RepositoryCount} repositories in database for owner: {Owner}", repositories.Count, owner);

            // Get all subdirectories in work folder
            string[] gitRepositoryPaths = Directory.GetDirectories(workFolderPath, ".git", SearchOption.AllDirectories);

            _logger.LogDebug("Found {DirectoryCount} directories to scan", gitRepositoryPaths.Length);

            int linkedCount = 0;

            foreach (string gitRepositoryPath in gitRepositoryPaths)
            {
                var gitRepositoryDirectory = new DirectoryInfo(gitRepositoryPath);

                string? gitRepositoryRootPath = gitRepositoryDirectory.Parent?.FullName;

                if (gitRepositoryRootPath is null)
                {
                    _logger.LogDebug("Could not determine parent directory for: {Directory}", gitRepositoryPath);
                    continue;
                }

                _logger.LogDebug("Checking git repository at: {Directory}", gitRepositoryPath);

                // Get remote URL using LibGit2Sharp
                string? remoteUrl = GetGitRemoteUrl(gitRepositoryRootPath);
                if (string.IsNullOrEmpty(remoteUrl))
                {
                    _logger.LogDebug("No remote URL found for repository at: {Directory}", gitRepositoryRootPath);
                    continue;
                }

                _logger.LogDebug("Found remote URL: {RemoteUrl} for directory: {Directory}", remoteUrl, gitRepositoryRootPath);
                // Match with GitHub repositories
                var matchedRepo = repositories.FirstOrDefault(r => IsRemoteUrlMatch(r.HtmlUrl, remoteUrl));
                if (matchedRepo != null)
                {
                    _logger.LogInformation("Matched repository {FullName} to local path: {LocalPath}", matchedRepo.FullName, gitRepositoryRootPath);
                    await _databaseService.UpdateRepositoryLocalPathAsync(matchedRepo.Id, gitRepositoryRootPath);
                    linkedCount++;
                }
            }

            _logger.LogInformation("Repository scan completed. Linked {LinkedCount} repositories", linkedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning repositories in folder: {WorkFolderPath}", workFolderPath);
            throw;
        }
    }

    private string? GetGitRemoteUrl(string repositoryPath)
    {
        try
        {
            if (!Repository.IsValid(repositoryPath))
            {
                _logger.LogDebug("Path is not a valid git repository: {RepositoryPath}", repositoryPath);
                return null;
            }

            using var repo = new Repository(repositoryPath);
            var origin = repo.Network.Remotes["origin"];

            if (origin == null)
            {
                _logger.LogDebug("No 'origin' remote found for repository: {RepositoryPath}", repositoryPath);
                return null;
            }

            _logger.LogTrace("Found origin remote URL: {RemoteUrl}", origin.Url);
            return origin.Url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting git remote URL for repository: {RepositoryPath}", repositoryPath);
            return null;
        }
    }

    private Credentials GetGitHubCredentials()
    {
        // Use the injected GitHub service to get the token
        var token = _githubService?.Configuration?.Token;

        if (!string.IsNullOrEmpty(token))
        {
            _logger.LogDebug("Using GitHub token for authentication");
            // For GitHub, use the token as username with empty password
            return new UsernamePasswordCredentials
            {
                Username = token,
                Password = string.Empty
            };
        }

        _logger.LogWarning("No GitHub token found in configuration, attempting default credentials");
        return new DefaultCredentials();
    }

    private static bool IsRemoteUrlMatch(string githubUrl, string remoteUrl)
    {
        // Normalize URLs for comparison
        string? normalizedGithubUrl = NormalizeGitHubUrl(githubUrl);
        string? normalizedRemoteUrl = NormalizeGitHubUrl(remoteUrl);

        // If either URL couldn't be normalized, they don't match
        if (normalizedGithubUrl is null || normalizedRemoteUrl is null)
            return false;

        return normalizedGithubUrl.Equals(normalizedRemoteUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeGitHubUrl(string url, bool stripGitSuffix = true)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimStart('/');

            if (stripGitSuffix && path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - 4);

            return string.IsNullOrEmpty(path) ? null : path;
        }

        var scpMatch = Regex.Match(url,
            @"^(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9._-]+):(?<path>.+)$");

        if (scpMatch.Success)
        {
            var path = scpMatch.Groups["path"].Value;

            path = path.TrimStart('/');

            if (stripGitSuffix && path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - 4);

            return string.IsNullOrEmpty(path) ? null : path;
        }

        return null;
    }

    public async Task<string?> GetLocalPathForRepositoryAsync(long repositoryId)
    {
        return await _databaseService.GetRepositoryLocalPathAsync(repositoryId);
    }

    public async Task<string?> CloneRepositoryAsync(string cloneUrl, string workFolderPath, string repositoryName, long repositoryId)
    {
        try
        {
            _logger.LogInformation("Cloning repository {RepositoryName} from {CloneUrl} to {WorkFolderPath}", repositoryName, cloneUrl, workFolderPath);

            if (!Directory.Exists(workFolderPath))
            {
                _logger.LogWarning("Work folder path does not exist: {WorkFolderPath}", workFolderPath);
                return null;
            }

            // Create target directory path
            var targetPath = Path.Combine(workFolderPath, repositoryName);

            // Check if directory already exists
            if (Directory.Exists(targetPath))
            {
                _logger.LogWarning("Directory already exists at: {TargetPath}", targetPath);

                // Check if it's a valid git repository
                if (LibGit2Sharp.Repository.IsValid(targetPath))
                {
                    _logger.LogInformation("Directory is already a valid git repository, updating local path in database");
                    await _databaseService.UpdateRepositoryLocalPathAsync(repositoryId, targetPath);
                    return targetPath;
                }
                else
                {
                    _logger.LogError("Directory exists but is not a valid git repository: {TargetPath}", targetPath);
                    return null;
                }
            }

            // Clone the repository using LibGit2Sharp
            _logger.LogDebug("Starting clone operation to: {TargetPath}", targetPath);

            // Configure clone options with authentication
            var cloneOptions = new CloneOptions
            {
                FetchOptions =
                {
                    CredentialsProvider = (_, _, _) => GetGitHubCredentials()
                }
            };

            var clonedPath = Repository.Clone(cloneUrl, targetPath, cloneOptions);

            _logger.LogInformation("Successfully cloned repository to: {ClonedPath}", clonedPath);

            // Update the database with the local path
            await _databaseService.UpdateRepositoryLocalPathAsync(repositoryId, targetPath);

            return targetPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cloning repository {RepositoryName} from {CloneUrl}", repositoryName, cloneUrl);
            return null;
        }
    }

    public async Task<bool> OpenInVSCodeAsync(string localPath)
    {
        try
        {
            _logger.LogInformation("Opening repository in VS Code: {LocalPath}", localPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"\"{localPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start VS Code process");
                return false;
            }

            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening repository in VS Code: {LocalPath}", localPath);
            return false;
        }
    }

    public async Task<bool> OpenInVisualStudioAsync(string localPath)
    {
        try
        {
            _logger.LogInformation("Opening repository in Visual Studio: {LocalPath}", localPath);

            // Look for solution files (.sln and .slnx)
            var slnFiles = Directory.GetFiles(localPath, "*.sln", SearchOption.AllDirectories);
            var slnxFiles = Directory.GetFiles(localPath, "*.slnx", SearchOption.AllDirectories);
            
            string targetPath;
            if (slnxFiles.Length > 0)
            {
                // Prefer .slnx (newer Visual Studio solution format)
                targetPath = slnxFiles[0];
                _logger.LogDebug("Found .slnx solution file: {SolutionFile}", targetPath);
            }
            else if (slnFiles.Length > 0)
            {
                // Fall back to .sln (classic solution format)
                targetPath = slnFiles[0];
                _logger.LogDebug("Found .sln solution file: {SolutionFile}", targetPath);
            }
            else
            {
                // If no solution file, just open the folder
                targetPath = localPath;
                _logger.LogDebug("No solution file found, opening folder");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start Visual Studio process");
                return false;
            }

            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening repository in Visual Studio: {LocalPath}", localPath);
            return false;
        }
    }

    public bool HasSolutionFile(string localPath)
    {
        try
        {
            if (!Directory.Exists(localPath))
            {
                _logger.LogDebug("Directory does not exist: {LocalPath}", localPath);
                return false;
            }

            // Check for .slnx files (newer format)
            var slnxFiles = Directory.GetFiles(localPath, "*.slnx", SearchOption.AllDirectories);
            if (slnxFiles.Length > 0)
            {
                _logger.LogDebug("Found .slnx solution file in: {LocalPath}", localPath);
                return true;
            }

            // Check for .sln files (classic format)
            var slnFiles = Directory.GetFiles(localPath, "*.sln", SearchOption.AllDirectories);
            if (slnFiles.Length > 0)
            {
                _logger.LogDebug("Found .sln solution file in: {LocalPath}", localPath);
                return true;
            }

            _logger.LogDebug("No solution files found in: {LocalPath}", localPath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for solution file in: {LocalPath}", localPath);
            return false;
        }
    }

    public async Task<bool> OpenInFileExplorerAsync(string localPath)
    {
        try
        {
            _logger.LogInformation("Opening folder in File Explorer: {LocalPath}", localPath);

            if (!Directory.Exists(localPath))
            {
                _logger.LogWarning("Directory does not exist: {LocalPath}", localPath);
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{localPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start File Explorer process");
                return false;
            }

            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening folder in File Explorer: {LocalPath}", localPath);
            return false;
        }
    }

    public async Task<bool> OpenInTerminalAsync(string localPath)
    {
        try
        {
            _logger.LogInformation("Opening folder in Terminal: {LocalPath}", localPath);

            if (!Directory.Exists(localPath))
            {
                _logger.LogWarning("Directory does not exist: {LocalPath}", localPath);
                return false;
            }

            // Use Windows Terminal if available, otherwise fall back to PowerShell
            var startInfo = new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-d \"{localPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            try
            {
                var process = Process.Start(startInfo);
                if (process != null)
                {
                    _logger.LogDebug("Opened in Windows Terminal");
                    await Task.CompletedTask;
                    return true;
                }
            }
            catch
            {
                _logger.LogDebug("Windows Terminal not available, falling back to PowerShell");
            }

            // Fallback to PowerShell
            startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -Command \"cd '{localPath}'\"",
                UseShellExecute = true,
                CreateNoWindow = false
            };

            var psProcess = Process.Start(startInfo);
            if (psProcess == null)
            {
                _logger.LogWarning("Failed to start PowerShell process");
                return false;
            }

            _logger.LogDebug("Opened in PowerShell");
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening folder in Terminal: {LocalPath}", localPath);
            return false;
        }
    }

    public async Task<int> CleanupInvalidLinkedRepositoriesAsync(string owner)
    {
        _logger.LogInformation("Starting cleanup of invalid linked repositories for owner: {Owner}", owner);

        try
        {
            // Get all repositories for the owner from database
            var repositories = await _databaseService.GetGitHubRepositoriesAsync(owner);
            _logger.LogDebug("Found {RepositoryCount} repositories in database for owner: {Owner}", repositories.Count, owner);

            int cleanedCount = 0;

            foreach (var repository in repositories)
            {
                // Skip repositories without local paths
                if (string.IsNullOrEmpty(repository.LocalPath))
                {
                    continue;
                }

                _logger.LogDebug("Checking local path for repository {FullName}: {LocalPath}", repository.FullName, repository.LocalPath);

                // Check if the directory still exists
                if (!Directory.Exists(repository.LocalPath))
                {
                    _logger.LogInformation("Local path no longer exists for repository {FullName}: {LocalPath}. Clearing link.", 
                        repository.FullName, repository.LocalPath);
                    
                    // Clear the local path in database
                    await _databaseService.UpdateRepositoryLocalPathAsync(repository.Id, null);
                    cleanedCount++;
                    continue;
                }

                // Check if it's still a valid git repository
                if (!Repository.IsValid(repository.LocalPath))
                {
                    _logger.LogInformation("Local path is no longer a valid git repository for {FullName}: {LocalPath}. Clearing link.", 
                        repository.FullName, repository.LocalPath);
                    
                    // Clear the local path in database
                    await _databaseService.UpdateRepositoryLocalPathAsync(repository.Id, null);
                    cleanedCount++;
                    continue;
                }

                // Optionally verify the remote URL still matches
                var remoteUrl = GetGitRemoteUrl(repository.LocalPath);
                if (!string.IsNullOrEmpty(remoteUrl))
                {
                    if (!IsRemoteUrlMatch(repository.HtmlUrl, remoteUrl))
                    {
                        _logger.LogWarning("Remote URL mismatch for repository {FullName}. Expected: {ExpectedUrl}, Found: {ActualUrl}. Clearing link.",
                            repository.FullName, repository.HtmlUrl, remoteUrl);
                        
                        // Clear the local path in database
                        await _databaseService.UpdateRepositoryLocalPathAsync(repository.Id, null);
                        cleanedCount++;
                    }
                }
                else
                {
                    _logger.LogWarning("Could not get remote URL for repository {FullName} at {LocalPath}. Clearing link.",
                        repository.FullName, repository.LocalPath);
                    
                    // Clear the local path in database
                    await _databaseService.UpdateRepositoryLocalPathAsync(repository.Id, null);
                    cleanedCount++;
                }
            }

            _logger.LogInformation("Cleanup completed. Cleared {CleanedCount} invalid repository links", cleanedCount);
            return cleanedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cleanup of invalid linked repositories for owner: {Owner}", owner);
            throw;
        }
    }
}
