using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("UpdateServer")]
[assembly: AssemblyDescription("Updater for pug and get5 that syncs repository files without requiring Git.")]
[assembly: AssemblyCompany("Qwepplz")]
[assembly: AssemblyProduct("UpdateServer")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Qwepplz")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("v1.0.0")]

internal static class UpdateServerProgram
{
    private const string GithubOwner = "Qwepplz";
    private const string MirrorOwner = "SaUrrr";
    private const int RequestTimeoutMs = 15000;
    private const string LogDirectoryName = "log";
    private const string LogFilePrefix = "UpdateServer-";
    private const string LogFileDateFormat = "yyyy-MM-dd";
    private const string LogFileExtension = ".log";
    private static readonly RepositoryTarget PugRepository = new RepositoryTarget("pug", GithubOwner, "pug", "pug", MirrorOwner, "pug");
    private static readonly RepositoryTarget Get5Repository = new RepositoryTarget("get5", GithubOwner, "get5", "get5", MirrorOwner, "get5");
    private static readonly RepositoryTarget[] AllRepositories = new[] { PugRepository, Get5Repository };
    private static readonly HashSet<string> AlwaysSkippedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "addons/sourcemod/scripting/include/logdebug.inc",
        "addons/sourcemod/scripting/include/restorecvars.inc"
    };
    private static LogSession activeLog;

    public static int Main(string[] args)
    {
        string targetDir = GetFullPath(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        SyncMutexHandle mutexHandle = null;
        TryInitializeLogging(targetDir, args);

        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            List<RepositoryTarget> selectedRepositories = ShowStartupPrompt(targetDir);
            if (selectedRepositories.Count == 0)
            {
                PauseBeforeExit();
                return 0;
            }

            string targetHash = GetTargetHash(targetDir);
            mutexHandle = SyncMutexHandle.Acquire(targetHash);

            HashSet<string> protectedPaths = BuildProtectedPathSet(targetDir);
            int staleArtifactsRemoved = RemoveStaleUpdaterArtifacts(targetDir, protectedPaths);
            if (staleArtifactsRemoved > 0)
            {
                Console.WriteLine(string.Format("Cleaned leftover temp files: {0}", staleArtifactsRemoved));
            }

            string stateRoot = GetStateDirectory(targetDir, targetHash);
            SyncSummary totalSummary = new SyncSummary();
            foreach (RepositoryTarget repository in selectedRepositories)
            {
                totalSummary.Merge(SyncRepository(repository, targetDir, stateRoot, protectedPaths));
            }

            Console.WriteLine();
            Console.WriteLine(selectedRepositories.Count > 1 ? "All selected syncs complete." : "Sync complete.");
            PrintSyncSummary(totalSummary);
            PauseBeforeExit();
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine();
            Console.WriteLine("Sync failed.");
            Console.WriteLine(exception.Message);
            LogException(exception);
            PauseBeforeExit();
            return 1;
        }
        finally
        {
            if (mutexHandle != null)
            {
                mutexHandle.Dispose();
            }

            ShutdownLogging();
        }
    }

    private static SyncSummary SyncRepository(RepositoryTarget repository, string targetDir, string stateRoot, HashSet<string> protectedPaths)
    {
        string tempRoot = null;

        try
        {
            string repoStateDir = Path.Combine(stateRoot, repository.StateKey);
            Directory.CreateDirectory(repoStateDir);
            string manifestPath = Path.Combine(repoStateDir, "tracked-files.txt");
            string statePath = Path.Combine(repoStateDir, "sync-state.json");

            tempRoot = Path.Combine(Path.GetTempPath(), "PugGet5Sync_" + repository.StateKey + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            Console.WriteLine();
            Console.WriteLine(string.Format("=== {0}/{1} ({2}) ===", repository.GithubOwner, repository.GithubRepo, repository.DisplayName));
            Console.WriteLine("[1/4] Reading repository tree...");
            string defaultBranch = GetDefaultBranch(repository);
            TreeResult treeResult = GetRemoteTree(repository, new[] { defaultBranch, "main", "master" });
            Console.WriteLine(string.Format("       Branch: {0}", treeResult.Branch));
            Console.WriteLine(string.Format("       Source: {0}", treeResult.Source));

            ImportedState importedState = ImportSyncState(statePath, manifestPath);
            Dictionary<string, CachedFileState> cachedFiles = importedState.Files;
            Dictionary<string, CachedFileState> newCachedFiles = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, TreeEntry> remoteFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TreeEntry> excludedFiles = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
            List<string> skippedConflictFiles = new List<string>();

            foreach (TreeEntry entry in treeResult.Tree)
            {
                if (!string.Equals(entry.type, "blob", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativePath = NormalizeRelativePath(entry.path);
                if (IsExcludedRootFile(relativePath))
                {
                    excludedFiles[relativePath] = entry;
                }
                else if (IsAlwaysSkippedFile(relativePath))
                {
                    skippedConflictFiles.Add(relativePath);
                }
                else
                {
                    remoteFiles[relativePath] = entry;
                }
            }

            Console.WriteLine("[2/4] Removing repo README/LICENSE when safe...");
            int excludedRemoved = 0;
            int excludedKept = 0;
            List<string> sortedExcludedFiles = SortKeys(excludedFiles.Keys);
            for (int index = 0; index < sortedExcludedFiles.Count; index++)
            {
                string relativePath = sortedExcludedFiles[index];
                TreeEntry entry = excludedFiles[relativePath];
                string destinationPath = GetTargetPathFromRelative(targetDir, relativePath);
                string destinationFull = GetFullPath(destinationPath);

                if (protectedPaths.Contains(destinationFull))
                {
                    WriteLogOnlyLine("Skipped protected README/LICENSE file: " + relativePath);
                    continue;
                }

                if (!File.Exists(destinationPath))
                {
                    continue;
                }

                AssertSafeManagedPath(targetDir, destinationPath);

                bool matchesRemote = TestCachedRemoteMatch(relativePath, destinationPath, entry, cachedFiles);
                if (!matchesRemote)
                {
                    matchesRemote = TestLocalMatchesRemoteBlob(destinationPath, entry);
                }

                if (matchesRemote)
                {
                    File.Delete(destinationPath);
                    RemoveEmptyParentDirectories(destinationPath, targetDir);
                    excludedRemoved++;
                    WriteLogOnlyLine("Removed README/LICENSE file: " + relativePath);
                }
                else
                {
                    excludedKept++;
                    WriteLogOnlyLine("Kept local README/LICENSE file: " + relativePath);
                }
            }

            foreach (string relativePath in SortKeys(skippedConflictFiles))
            {
                WriteLogOnlyLine("Skipped compile-only conflict file: " + relativePath);
            }

            Console.WriteLine("[3/4] Downloading and updating files...");
            int added = 0;
            int updated = 0;
            int unchanged = 0;
            List<string> newManifest = new List<string>();
            List<string> sortedRemoteFiles = SortKeys(remoteFiles.Keys);
            using (ProgressDisplay progress = CreateProgressDisplay())
            {
                for (int index = 0; index < sortedRemoteFiles.Count; index++)
                {
                    string relativePath = sortedRemoteFiles[index];
                    TreeEntry entry = remoteFiles[relativePath];
                    string destinationPath = GetTargetPathFromRelative(targetDir, relativePath);
                    string destinationFull = GetFullPath(destinationPath);
                    progress.Update(
                        FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | checking", added, updated, unchanged)),
                        FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));

                    if (protectedPaths.Contains(destinationFull))
                    {
                        WriteLogOnlyLine("Skipped protected updater file: " + relativePath);
                        continue;
                    }

                    newManifest.Add(relativePath);
                    AssertSafeManagedPath(targetDir, destinationPath);
                    AssertNoDirectoryConflict(destinationPath);

                    if (TestCachedRemoteMatch(relativePath, destinationPath, entry, cachedFiles))
                    {
                        newCachedFiles[relativePath] = GetLocalFileState(destinationPath, entry.sha);
                        unchanged++;
                        WriteLogOnlyLine("Cached match: " + relativePath);
                        continue;
                    }

                    bool existed = File.Exists(destinationPath);
                    if (existed && TestLocalMatchesRemoteBlob(destinationPath, entry))
                    {
                        newCachedFiles[relativePath] = GetLocalFileState(destinationPath, entry.sha);
                        unchanged++;
                        WriteLogOnlyLine("Verified match: " + relativePath);
                        continue;
                    }

                    string encodedPath = ConvertToUrlPath(relativePath);
                    List<string> downloadUrls = BuildRepositoryRawUrls(repository, treeResult.Branch, encodedPath);

                    progress.Update(
                        FormatProgressStatus("[3/4] Files", index + 1, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2} | downloading", added, updated, unchanged)),
                        FormatProgressBarLine(index + 1, sortedRemoteFiles.Count));
                    DownloadRemoteFile(downloadUrls, destinationPath, entry.sha, tempRoot);
                    newCachedFiles[relativePath] = GetLocalFileState(destinationPath, entry.sha);

                    if (existed)
                    {
                        updated++;
                        WriteLogOnlyLine("Updated: " + relativePath);
                    }
                    else
                    {
                        added++;
                        WriteLogOnlyLine("Added: " + relativePath);
                    }
                }

                progress.Complete(
                    FormatProgressStatus("[3/4] Files", sortedRemoteFiles.Count, sortedRemoteFiles.Count, string.Format("added: {0} updated: {1} unchanged: {2}", added, updated, unchanged)),
                    FormatProgressBarLine(sortedRemoteFiles.Count, sortedRemoteFiles.Count));
            }

            Console.WriteLine("[4/4] Removing files deleted upstream...");
            List<string> oldManifest = new List<string>(importedState.TrackedFiles);
            if (oldManifest.Count == 0 && File.Exists(manifestPath))
            {
                oldManifest = ReadManifest(manifestPath);
            }

            HashSet<string> remoteSet = new HashSet<string>(newManifest, StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            for (int index = 0; index < oldManifest.Count; index++)
            {
                string relativePath = oldManifest[index];
                if (remoteSet.Contains(relativePath))
                {
                    continue;
                }

                if (IsAlwaysSkippedFile(relativePath))
                {
                    continue;
                }

                string destinationPath = GetTargetPathFromRelative(targetDir, relativePath);
                string destinationFull = GetFullPath(destinationPath);

                if (protectedPaths.Contains(destinationFull))
                {
                    WriteLogOnlyLine("Skipped protected stale file: " + relativePath);
                    continue;
                }

                if (!File.Exists(destinationPath))
                {
                    continue;
                }

                AssertSafeManagedPath(targetDir, destinationPath);
                File.Delete(destinationPath);
                RemoveEmptyParentDirectories(destinationPath, targetDir);
                removed++;
                WriteLogOnlyLine("Removed upstream-deleted file: " + relativePath);
            }

            List<string> sortedManifest = newManifest.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllLines(manifestPath, sortedManifest.ToArray(), new UTF8Encoding(false));
            ExportSyncState(statePath, sortedManifest, newCachedFiles);

            return new SyncSummary
            {
                Added = added,
                Updated = updated,
                Removed = removed,
                ExcludedRemoved = excludedRemoved,
                Unchanged = unchanged,
                SkippedConflictFiles = new HashSet<string>(skippedConflictFiles, StringComparer.OrdinalIgnoreCase)
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempRoot) && Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, true);
                }
                catch
                {
                }
            }
        }
    }

    private static void PrintSyncSummary(SyncSummary summary)
    {
        Console.WriteLine(string.Format("Added: {0}", summary.Added));
        Console.WriteLine(string.Format("Updated: {0}", summary.Updated));
        Console.WriteLine(string.Format("Removed: {0}", summary.Removed));
        Console.WriteLine(string.Format("Unchanged: {0}", summary.Unchanged));
    }

    private static string FormatProgressStatus(string stageLabel, int current, int total, string metrics)
    {
        return string.Format("{0} {1}/{2} | {3}", stageLabel, Math.Max(0, current), Math.Max(0, total), metrics);
    }

    private static string FormatProgressBarLine(int current, int total)
    {
        int safeCurrent = Math.Max(0, current);
        int safeTotal = Math.Max(0, total);
        string suffix = string.Format(" {0}/{1}", safeCurrent, safeTotal);
        int width = GetProgressBarWidth(suffix.Length);
        return BuildProgressBar(safeCurrent, safeTotal, width) + suffix;
    }

    private static string BuildProgressBar(int current, int total, int width)
    {
        int safeWidth = Math.Max(8, width);
        int safeCurrent = Math.Max(0, current);
        int safeTotal = Math.Max(0, total);
        int filled = safeTotal <= 0
            ? safeWidth
            : Math.Min(safeWidth, (int)Math.Round((double)Math.Min(safeCurrent, safeTotal) * safeWidth / safeTotal, MidpointRounding.AwayFromZero));

        return "[" + new string('#', filled) + new string('-', safeWidth - filled) + "]";
    }

    private static int GetProgressBarWidth(int suffixLength)
    {
        try
        {
            int lineWidth = Math.Max(20, Console.BufferWidth - 1);
            return Math.Max(8, lineWidth - suffixLength - 2);
        }
        catch
        {
            return 48;
        }
    }

    private static ProgressDisplay CreateProgressDisplay()
    {
        TextWriter outputWriter = activeLog == null ? Console.Out : activeLog.ConsoleWriter;
        return new ProgressDisplay(outputWriter, CanRefreshProgressDisplay());
    }

    private static bool CanRefreshProgressDisplay()
    {
        try
        {
            return Environment.UserInteractive && !Console.IsOutputRedirected;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteLogOnlyLine(string message)
    {
        if (activeLog == null)
        {
            return;
        }

        try
        {
            activeLog.WriteLogOnlyLine(message);
        }
        catch
        {
        }
    }

    private static HashSet<string> BuildProtectedPathSet(string targetDir)
    {
        HashSet<string> protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string executablePath = GetExecutablePath();
        if (!string.IsNullOrEmpty(executablePath))
        {
            protectedPaths.Add(GetFullPath(executablePath));
        }

        string[] knownHelperNames = new[]
        {
            "_UpdateServer.bat",
            "_UpdateServer.ps1",
            "UpdateServer.cs",
            "Build-UpdateServer.bat",
            "Build-UpdateServer.cmd",
            "UpdateServer.exe"
        };

        foreach (string helperName in knownHelperNames)
        {
            string fullPath = GetFullPath(Path.Combine(targetDir, helperName));
            if (File.Exists(fullPath))
            {
                protectedPaths.Add(fullPath);
            }
        }

        string logDirectoryPath = GetLogDirectoryPath(targetDir);
        try
        {
            if (Directory.Exists(logDirectoryPath) && (File.GetAttributes(logDirectoryPath) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (string logFilePath in EnumerateFilesSafely(logDirectoryPath))
                {
                    protectedPaths.Add(GetFullPath(logFilePath));
                }
            }
        }
        catch
        {
        }

        return protectedPaths;
    }

    private static string GetLogDirectoryPath(string targetDir)
    {
        return GetFullPath(Path.Combine(targetDir, LogDirectoryName));
    }

    private static string GetExecutablePath()
    {
        try
        {
            return GetFullPath(System.Reflection.Assembly.GetEntryAssembly().Location);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<RepositoryTarget> ShowStartupPrompt(string targetDir)
    {
        Console.WriteLine("Pug/Get5 updater");
        Console.WriteLine();
        Console.WriteLine("Target folder:");
        Console.WriteLine(targetDir);
        Console.WriteLine();
        Console.WriteLine("This will sync upstream changes into the current folder.");
        Console.WriteLine("Choose what to sync:");
        Console.WriteLine("1 - pug  (Qwepplz/pug)");
        Console.WriteLine("2 - get5 (Qwepplz/get5)");
        Console.WriteLine("3 - all");
        Console.WriteLine("Press ESC to exit immediately.");
        Console.WriteLine();

        try
        {
            ConsoleKeyInfo keyInfo;
            while (true)
            {
                keyInfo = Console.ReadKey(true);
                if (keyInfo.Key == ConsoleKey.D1 || keyInfo.Key == ConsoleKey.NumPad1)
                {
                    Console.WriteLine("Selected: pug");
                    Console.WriteLine("Starting sync...");
                    Console.WriteLine();
                    return new List<RepositoryTarget> { PugRepository };
                }

                if (keyInfo.Key == ConsoleKey.D2 || keyInfo.Key == ConsoleKey.NumPad2)
                {
                    Console.WriteLine("Selected: get5");
                    Console.WriteLine("Starting sync...");
                    Console.WriteLine();
                    return new List<RepositoryTarget> { Get5Repository };
                }

                if (keyInfo.Key == ConsoleKey.D3 || keyInfo.Key == ConsoleKey.NumPad3)
                {
                    Console.WriteLine("Selected: all");
                    Console.WriteLine("Starting sync...");
                    Console.WriteLine();
                    return new List<RepositoryTarget>(AllRepositories);
                }

                if (keyInfo.Key == ConsoleKey.Escape)
                {
                    Console.WriteLine("Exited by user.");
                    return new List<RepositoryTarget>();
                }
            }
        }
        catch
        {
            return new List<RepositoryTarget>(AllRepositories);
        }
    }

    private static void PauseBeforeExit()
    {
        try
        {
            if (Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                Console.WriteLine();
                Console.Write("Press any key to continue . . .");
                Console.ReadKey(true);
                Console.WriteLine();
            }
        }
        catch
        {
        }
    }

    private static void TryInitializeLogging(string targetDir, string[] args)
    {
        if (activeLog != null)
        {
            return;
        }

        try
        {
            activeLog = LogSession.Create(targetDir);
            activeLog.Attach();
            activeLog.WriteSessionStart(targetDir, args);
            Console.WriteLine(string.Format("Log file: {0}", activeLog.CurrentLogPath));
        }
        catch
        {
            if (activeLog != null)
            {
                try
                {
                    activeLog.Dispose();
                }
                catch
                {
                }

                activeLog = null;
            }
        }
    }

    private static void ShutdownLogging()
    {
        if (activeLog == null)
        {
            return;
        }

        try
        {
            activeLog.Dispose();
        }
        catch
        {
        }
        finally
        {
            activeLog = null;
        }
    }

    private static void LogException(Exception exception)
    {
        if (activeLog == null || exception == null)
        {
            return;
        }

        try
        {
            activeLog.WriteLogOnlyLine("Unhandled exception:");
            activeLog.WriteLogOnlyLine(exception.ToString());
        }
        catch
        {
        }
    }

    private static string GetFullPath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/');
    }

    private static string GetTargetHash(string targetDir)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] bytes = Encoding.UTF8.GetBytes(GetFullPath(targetDir).ToLowerInvariant());
            byte[] hash = sha256.ComputeHash(bytes);
            return ToHexString(hash);
        }
    }

    private static string GetStateDirectory(string targetDir, string targetHash)
    {
        List<string> baseCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUG_GET5_SYNC_STATE")))
        {
            baseCandidates.Add(Environment.GetEnvironmentVariable("PUG_GET5_SYNC_STATE"));
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOCALAPPDATA")))
        {
            baseCandidates.Add(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "PugGet5Sync"));
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPDATA")))
        {
            baseCandidates.Add(Path.Combine(Environment.GetEnvironmentVariable("APPDATA"), "PugGet5Sync"));
        }

        baseCandidates.Add(Path.Combine(Path.GetTempPath(), "PugGet5Sync"));

        Exception lastError = null;
        foreach (string candidate in baseCandidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                string stateDir = Path.Combine(candidate, targetHash);
                Directory.CreateDirectory(stateDir);
                return stateDir;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
        }

        throw new InvalidOperationException(string.Format("Cannot create sync state directory. Last error: {0}", lastError == null ? "Unknown error" : lastError.Message));
    }

    private static string GetTargetPathFromRelative(string targetDir, string relativePath)
    {
        string windowsRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(targetDir, windowsRelative);
    }

    private static string ConvertToUrlPath(string relativePath)
    {
        string[] segments = NormalizeRelativePath(relativePath).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join("/", segments.Select(Uri.EscapeDataString).ToArray());
    }

    private static bool IsExcludedRootFile(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        if (normalized.IndexOf('/') >= 0)
        {
            return false;
        }

        string fileName = Path.GetFileName(normalized);
        return fileName.StartsWith("README", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("LECENSE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlwaysSkippedFile(string relativePath)
    {
        return AlwaysSkippedFiles.Contains(NormalizeRelativePath(relativePath));
    }

    private static string ComputeGitBlobSha1(string path)
    {
        FileInfo fileInfo = new FileInfo(path);
        byte[] prefixBytes = Encoding.ASCII.GetBytes("blob " + fileInfo.Length + "\0");

        using (SHA1 sha1 = SHA1.Create())
        using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            sha1.TransformBlock(prefixBytes, 0, prefixBytes.Length, null, 0);
            byte[] buffer = new byte[81920];
            int bytesRead;

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha1.TransformBlock(buffer, 0, bytesRead, null, 0);
            }

            sha1.TransformFinalBlock(new byte[0], 0, 0);
            return ToHexString(sha1.Hash);
        }
    }

    private static bool TestLocalMatchesRemoteBlob(string path, TreeEntry entry)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        FileInfo fileInfo = new FileInfo(path);
        if (entry.size > 0 && fileInfo.Length != entry.size)
        {
            return false;
        }

        string localSha = ComputeGitBlobSha1(path);
        return string.Equals(localSha, entry.sha, StringComparison.OrdinalIgnoreCase);
    }

    private static CachedFileState GetLocalFileState(string path, string remoteSha)
    {
        FileInfo fileInfo = new FileInfo(path);
        return new CachedFileState
        {
            path = NormalizeRelativePath(path),
            remote_sha = remoteSha,
            length = fileInfo.Length,
            last_write_utc_ticks = fileInfo.LastWriteTimeUtc.Ticks
        };
    }

    private static bool TestCachedRemoteMatch(string relativePath, string path, TreeEntry entry, Dictionary<string, CachedFileState> cachedFiles)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        CachedFileState cached;
        if (!cachedFiles.TryGetValue(relativePath, out cached) || cached == null)
        {
            return false;
        }

        if (!string.Equals(cached.remote_sha, entry.sha, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        FileInfo fileInfo = new FileInfo(path);
        return cached.length == fileInfo.Length && cached.last_write_utc_ticks == fileInfo.LastWriteTimeUtc.Ticks;
    }

    private static ImportedState ImportSyncState(string statePath, string legacyManifestPath)
    {
        ImportedState importedState = new ImportedState();

        if (File.Exists(statePath))
        {
            try
            {
                JavaScriptSerializer serializer = CreateSerializer();
                string raw = File.ReadAllText(statePath, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    SyncState state = serializer.Deserialize<SyncState>(raw);
                    if (state != null)
                    {
                        if (state.tracked_files != null)
                        {
                            importedState.TrackedFiles = state.tracked_files.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
                        }

                        if (state.files != null)
                        {
                            foreach (CachedFileState file in state.files)
                            {
                                if (file == null || string.IsNullOrWhiteSpace(file.path))
                                {
                                    continue;
                                }

                                importedState.Files[NormalizeRelativePath(file.path)] = file;
                            }
                        }

                        return importedState;
                    }
                }
            }
            catch
            {
                Console.WriteLine("       Sync cache unreadable. Rebuilding...");
            }
        }

        if (File.Exists(legacyManifestPath))
        {
            importedState.TrackedFiles = ReadManifest(legacyManifestPath);
        }

        return importedState;
    }

    private static void ExportSyncState(string statePath, List<string> trackedFiles, Dictionary<string, CachedFileState> files)
    {
        List<CachedFileState> fileStates = new List<CachedFileState>();
        foreach (string path in SortKeys(files.Keys))
        {
            CachedFileState fileState = files[path];
            fileState.path = NormalizeRelativePath(path);
            fileStates.Add(fileState);
        }

        SyncState state = new SyncState
        {
            version = 1,
            tracked_files = trackedFiles.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
            files = fileStates
        };

        JavaScriptSerializer serializer = CreateSerializer();
        string json = FormatJsonForReadability(serializer.Serialize(state));
        File.WriteAllText(statePath, json, new UTF8Encoding(false));
    }

    private static JavaScriptSerializer CreateSerializer()
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;
        serializer.RecursionLimit = 256;
        return serializer;
    }

    private static string FormatJsonForReadability(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        StringBuilder builder = new StringBuilder(json.Length + (json.Length / 4));
        int indentLevel = 0;
        bool inString = false;
        bool escaping = false;
        char previousNonWhitespace = '\0';

        foreach (char character in json)
        {
            if (escaping)
            {
                builder.Append(character);
                escaping = false;
                continue;
            }

            if (inString)
            {
                builder.Append(character);
                if (character == '\\')
                {
                    escaping = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    builder.Append(character);
                    inString = true;
                    break;

                case '{':
                case '[':
                    builder.Append(character);
                    builder.AppendLine();
                    indentLevel++;
                    AppendJsonIndentation(builder, indentLevel);
                    break;

                case '}':
                case ']':
                    indentLevel = Math.Max(0, indentLevel - 1);
                    if (previousNonWhitespace != '{' && previousNonWhitespace != '[')
                    {
                        builder.AppendLine();
                        AppendJsonIndentation(builder, indentLevel);
                    }

                    builder.Append(character);
                    break;

                case ',':
                    builder.Append(character);
                    builder.AppendLine();
                    AppendJsonIndentation(builder, indentLevel);
                    break;

                case ':':
                    builder.Append(": ");
                    break;

                default:
                    if (!char.IsWhiteSpace(character))
                    {
                        builder.Append(character);
                    }
                    break;
            }

            if (!char.IsWhiteSpace(character))
            {
                previousNonWhitespace = character;
            }
        }

        return builder.ToString();
    }

    private static void AppendJsonIndentation(StringBuilder builder, int indentLevel)
    {
        builder.Append(' ', indentLevel * 2);
    }

    private static List<string> BuildRepositoryInfoUrls(RepositoryTarget repository)
    {
        List<string> urls = new List<string>();
        urls.Add(string.Format("https://api.github.com/repos/{0}/{1}", repository.GithubOwner, repository.GithubRepo));
        if (repository.HasMirror)
        {
            urls.Add(string.Format("https://gitee.com/api/v5/repos/{0}/{1}", repository.MirrorOwner, repository.MirrorRepo));
        }

        return urls;
    }

    private static List<string> BuildRepositoryTreeUrls(RepositoryTarget repository, string encodedBranch)
    {
        List<string> urls = new List<string>();
        urls.Add(string.Format("https://api.github.com/repos/{0}/{1}/git/trees/{2}?recursive=1", repository.GithubOwner, repository.GithubRepo, encodedBranch));
        if (repository.HasMirror)
        {
            urls.Add(string.Format("https://gitee.com/api/v5/repos/{0}/{1}/git/trees/{2}?recursive=1", repository.MirrorOwner, repository.MirrorRepo, encodedBranch));
        }

        return urls;
    }

    private static List<string> BuildRepositoryRawUrls(RepositoryTarget repository, string branch, string encodedPath)
    {
        List<string> urls = new List<string>();
        urls.Add(string.Format("https://raw.githubusercontent.com/{0}/{1}/{2}/{3}", repository.GithubOwner, repository.GithubRepo, branch, encodedPath));
        if (repository.HasMirror)
        {
            urls.Add(string.Format("https://gitee.com/{0}/{1}/raw/{2}/{3}", repository.MirrorOwner, repository.MirrorRepo, branch, encodedPath));
        }

        return urls;
    }

    private static JsonResponse<T> RequestJsonFromUrls<T>(IEnumerable<string> urls)
    {
        List<string> errors = new List<string>();
        foreach (string url in urls)
        {
            try
            {
                string content = DownloadString(url, "application/json");
                JavaScriptSerializer serializer = CreateSerializer();
                T value = serializer.Deserialize<T>(content);
                return new JsonResponse<T> { Url = url, Value = value };
            }
            catch (Exception exception)
            {
                errors.Add(url + " => " + exception.Message);
            }
        }

        throw new InvalidOperationException("All repository API requests failed." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
    }

    private static string GetDefaultBranch(RepositoryTarget repository)
    {
        List<string> urls = BuildRepositoryInfoUrls(repository);

        try
        {
            JsonResponse<RepoInfo> response = RequestJsonFromUrls<RepoInfo>(urls);
            if (response.Value != null && !string.IsNullOrWhiteSpace(response.Value.default_branch))
            {
                return response.Value.default_branch;
            }
        }
        catch
        {
            Console.WriteLine("       Default branch lookup failed, trying common branch names.");
        }

        return "main";
    }

    private static TreeResult GetRemoteTree(RepositoryTarget repository, IEnumerable<string> branchCandidates)
    {
        List<string> errors = new List<string>();
        HashSet<string> seenBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string branch in branchCandidates)
        {
            if (string.IsNullOrWhiteSpace(branch) || !seenBranches.Add(branch))
            {
                continue;
            }

            Console.WriteLine(string.Format("       Reading branch: {0}", branch));
            string encodedBranch = Uri.EscapeDataString(branch);
            List<string> urls = BuildRepositoryTreeUrls(repository, encodedBranch);

            try
            {
                JsonResponse<TreeResponse> response = RequestJsonFromUrls<TreeResponse>(urls);
                TreeResponse tree = response.Value;
                if (tree == null)
                {
                    throw new InvalidOperationException("Repository API returned no file tree.");
                }

                if (tree.truncated)
                {
                    throw new InvalidOperationException("Repository API returned a truncated tree. Refusing to sync because deletion would be unsafe.");
                }

                if (tree.tree == null)
                {
                    throw new InvalidOperationException("Repository API returned no file tree.");
                }

                return new TreeResult
                {
                    Branch = branch,
                    Source = response.Url,
                    Tree = tree.tree
                };
            }
            catch (Exception exception)
            {
                errors.Add(branch + " => " + exception.Message);
            }
        }

        throw new InvalidOperationException("Cannot read repository tree." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
    }

    private static string DownloadString(string url, string accept)
    {
        HttpWebRequest request = CreateRequest(url, accept);
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (Stream stream = response.GetResponseStream())
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    private static void DownloadToFile(string url, string destination)
    {
        HttpWebRequest request = CreateRequest(url, "application/octet-stream, */*");
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (Stream stream = response.GetResponseStream())
        using (FileStream fileStream = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.CopyTo(fileStream);
        }
    }

    private static HttpWebRequest CreateRequest(string url, string accept)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.UserAgent = "PugGet5Sync";
        request.Accept = accept;
        request.Timeout = RequestTimeoutMs;
        request.ReadWriteTimeout = RequestTimeoutMs;
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        request.Proxy = WebRequest.DefaultWebProxy;
        return request;
    }

    private static void DownloadRemoteFile(IEnumerable<string> urls, string destination, string expectedBlobSha, string tempRoot)
    {
        List<string> errors = new List<string>();

        foreach (string url in urls)
        {
            string tempPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                DownloadToFile(url, tempPath);
                string actualSha = ComputeGitBlobSha1(tempPath);
                if (!string.Equals(actualSha, expectedBlobSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Format("Downloaded file SHA mismatch. Expected {0}, got {1}", expectedBlobSha, actualSha));
                }

                WriteFileAtomically(tempPath, destination);
                File.Delete(tempPath);
                return;
            }
            catch (Exception exception)
            {
                errors.Add(url + " => " + exception.Message);
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        throw new InvalidOperationException("All download attempts failed for " + destination + "." + Environment.NewLine + string.Join(Environment.NewLine, errors.ToArray()));
    }

    private static void WriteFileAtomically(string sourcePath, string destinationPath)
    {
        string parent = Path.GetDirectoryName(destinationPath);
        if (!Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string fileName = Path.GetFileName(destinationPath);
        string stagingPath = Path.Combine(parent, fileName + ".__pug_get5_sync_staging__" + Guid.NewGuid().ToString("N"));
        string backupPath = Path.Combine(parent, fileName + ".__pug_get5_sync_backup__" + Guid.NewGuid().ToString("N"));

        try
        {
            File.Copy(sourcePath, stagingPath, true);
            if (File.Exists(destinationPath))
            {
                File.Replace(stagingPath, destinationPath, backupPath, true);
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            else
            {
                File.Move(stagingPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                try
                {
                    File.Delete(stagingPath);
                }
                catch
                {
                }
            }

            if (File.Exists(backupPath))
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch
                {
                }
            }
        }
    }

    private static int RemoveStaleUpdaterArtifacts(string targetDir, HashSet<string> protectedPaths)
    {
        List<string> artifactPaths = new List<string>();
        foreach (string path in EnumerateFilesSafely(targetDir))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.IndexOf(".__pug_get5_sync_staging__", StringComparison.OrdinalIgnoreCase) >= 0
                || fileName.IndexOf(".__pug_get5_sync_backup__", StringComparison.OrdinalIgnoreCase) >= 0
                || fileName.IndexOf(".__betterbot_sync_staging__", StringComparison.OrdinalIgnoreCase) >= 0
                || fileName.IndexOf(".__betterbot_sync_backup__", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                artifactPaths.Add(path);
            }
        }

        int removedCount = 0;
        foreach (string path in artifactPaths.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            string fullPath = GetFullPath(path);
            if (protectedPaths.Contains(fullPath))
            {
                continue;
            }

            AssertSafeManagedPath(targetDir, fullPath);
            File.Delete(fullPath);
            RemoveEmptyParentDirectories(fullPath, targetDir);
            removedCount++;
        }

        return removedCount;
    }

    private static void AssertNoDirectoryConflict(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException("Cannot place file because a directory exists at: " + path);
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string rootDir)
    {
        Stack<string> pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootDir);

        while (pendingDirectories.Count > 0)
        {
            string currentDir = pendingDirectories.Pop();

            IEnumerable<string> files = Enumerable.Empty<string>();
            try
            {
                files = Directory.EnumerateFiles(currentDir);
            }
            catch
            {
            }

            foreach (string file in files)
            {
                yield return file;
            }

            IEnumerable<string> subDirectories = Enumerable.Empty<string>();
            try
            {
                subDirectories = Directory.EnumerateDirectories(currentDir);
            }
            catch
            {
            }

            foreach (string subDirectory in subDirectories)
            {
                try
                {
                    if ((File.GetAttributes(subDirectory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pendingDirectories.Push(subDirectory);
                }
                catch
                {
                }
            }
        }
    }

    private static void AssertSafeManagedPath(string targetDir, string path)
    {
        string targetRoot = GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = GetFullPath(path);

        if (!IsPathWithinTarget(targetRoot, fullPath))
        {
            throw new InvalidOperationException("Refusing to touch a path outside the target folder: " + fullPath);
        }

        string current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            bool exists = Directory.Exists(current) || File.Exists(current);
            if (exists)
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Refusing to touch a reparse point path: " + current);
                }
            }

            string trimmedCurrent = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmedCurrent, targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(trimmedCurrent);
        }
    }

    private static bool IsPathWithinTarget(string targetRoot, string fullPath)
    {
        string normalizedTarget = targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedTarget, normalizedFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = normalizedTarget + Path.DirectorySeparatorChar;
        return normalizedFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveEmptyParentDirectories(string filePath, string stopAt)
    {
        string stopFull = GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = Path.GetDirectoryName(filePath);

        while (!string.IsNullOrEmpty(current))
        {
            string currentFull = GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (currentFull.Length <= stopFull.Length)
            {
                break;
            }

            if (Directory.EnumerateFileSystemEntries(currentFull).Any())
            {
                break;
            }

            Directory.Delete(currentFull, false);
            current = Path.GetDirectoryName(currentFull);
        }
    }

    private static List<string> ReadManifest(string manifestPath)
    {
        return File.ReadAllLines(manifestPath)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeRelativePath)
            .ToList();
    }

    private static List<string> SortKeys(IEnumerable<string> keys)
    {
        return keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ToHexString(byte[] bytes)
    {
        StringBuilder builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(value.ToString("x2"));
        }

        return builder.ToString();
    }

    private sealed class SyncMutexHandle : IDisposable
    {
        private readonly Mutex mutex;
        private bool disposed;

        private SyncMutexHandle(Mutex mutexInstance)
        {
            mutex = mutexInstance;
        }

        public static SyncMutexHandle Acquire(string targetHash)
        {
            bool createdNew = false;
            Mutex mutex = new Mutex(false, @"Local\PugGet5Sync_" + targetHash, out createdNew);
            bool acquired = false;

            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                throw new InvalidOperationException("Another Pug/Get5 sync is already running for this folder.");
            }

            return new SyncMutexHandle(mutex);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
            }

            mutex.Dispose();
        }
    }

    private sealed class RepositoryTarget
    {
        public readonly string DisplayName;
        public readonly string GithubOwner;
        public readonly string GithubRepo;
        public readonly string StateKey;
        public readonly string MirrorOwner;
        public readonly string MirrorRepo;

        public RepositoryTarget(string displayName, string githubOwner, string githubRepo, string stateKey, string mirrorOwner, string mirrorRepo)
        {
            DisplayName = displayName;
            GithubOwner = githubOwner;
            GithubRepo = githubRepo;
            StateKey = stateKey;
            MirrorOwner = mirrorOwner;
            MirrorRepo = mirrorRepo;
        }

        public bool HasMirror
        {
            get
            {
                return !string.IsNullOrWhiteSpace(MirrorOwner) && !string.IsNullOrWhiteSpace(MirrorRepo);
            }
        }
    }

    private sealed class SyncSummary
    {
        public int Added;
        public int Updated;
        public int Removed;
        public int ExcludedRemoved;
        public int Unchanged;
        public HashSet<string> SkippedConflictFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Merge(SyncSummary other)
        {
            if (other == null)
            {
                return;
            }

            Added += other.Added;
            Updated += other.Updated;
            Removed += other.Removed;
            ExcludedRemoved += other.ExcludedRemoved;
            Unchanged += other.Unchanged;

            foreach (string path in other.SkippedConflictFiles)
            {
                SkippedConflictFiles.Add(path);
            }
        }
    }

    private sealed class TreeResult
    {
        public string Branch;
        public string Source;
        public List<TreeEntry> Tree;
    }

    private sealed class JsonResponse<T>
    {
        public string Url;
        public T Value;
    }

    private sealed class RepoInfo
    {
        public string default_branch { get; set; }
    }

    private sealed class TreeResponse
    {
        public bool truncated { get; set; }
        public List<TreeEntry> tree { get; set; }
    }

    private sealed class TreeEntry
    {
        public string path { get; set; }
        public string type { get; set; }
        public string sha { get; set; }
        public long size { get; set; }
    }

    private sealed class SyncState
    {
        public int version { get; set; }
        public List<string> tracked_files { get; set; }
        public List<CachedFileState> files { get; set; }
    }

    private sealed class CachedFileState
    {
        public string path { get; set; }
        public string remote_sha { get; set; }
        public long length { get; set; }
        public long last_write_utc_ticks { get; set; }
    }

    private sealed class ImportedState
    {
        public List<string> TrackedFiles = new List<string>();
        public Dictionary<string, CachedFileState> Files = new Dictionary<string, CachedFileState>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LogSession : IDisposable
    {
        private readonly TextWriter originalOut;
        private readonly TextWriter originalError;
        private readonly DailyLogFileWriter dailyWriter;
        private readonly TimestampedFileWriter fileWriter;
        private bool attached;
        private bool disposed;

        private LogSession(TextWriter consoleOut, TextWriter consoleError, DailyLogFileWriter writer)
        {
            originalOut = consoleOut;
            originalError = consoleError;
            dailyWriter = writer;
            fileWriter = new TimestampedFileWriter(writer);
        }

        public string CurrentLogPath
        {
            get { return dailyWriter.CurrentPath; }
        }

        public TextWriter ConsoleWriter
        {
            get { return originalOut; }
        }

        public static LogSession Create(string targetDir)
        {
            DailyLogFileWriter writer = new DailyLogFileWriter(targetDir, GetLogDirectoryPath(targetDir));
            return new LogSession(Console.Out, Console.Error, writer);
        }

        public void Attach()
        {
            if (attached)
            {
                return;
            }

            Console.SetOut(new TeeTextWriter(originalOut, fileWriter));
            Console.SetError(new TeeTextWriter(originalError, fileWriter));
            attached = true;
        }

        public void WriteSessionStart(string targetDir, string[] args)
        {
            WriteLogOnlyLine(string.Empty);
            WriteLogOnlyLine("===== Session started =====");
            WriteLogOnlyLine("Target folder: " + targetDir);
            WriteLogOnlyLine("Arguments: " + FormatArguments(args));
        }

        public void WriteLogOnlyLine(string message)
        {
            fileWriter.WriteLine(message ?? string.Empty);
            fileWriter.Flush();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                WriteLogOnlyLine("===== Session ended =====");
                WriteLogOnlyLine(string.Empty);
            }
            catch
            {
            }

            if (attached)
            {
                try
                {
                    Console.Out.Flush();
                }
                catch
                {
                }

                try
                {
                    Console.Error.Flush();
                }
                catch
                {
                }

                Console.SetOut(originalOut);
                Console.SetError(originalError);
                attached = false;
            }

            fileWriter.Dispose();
            dailyWriter.Dispose();
        }

        private static string FormatArguments(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "(none)";
            }

            return string.Join(" ", args);
        }
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter primary;
        private readonly TextWriter secondary;

        public TeeTextWriter(TextWriter primaryWriter, TextWriter secondaryWriter)
        {
            primary = primaryWriter;
            secondary = secondaryWriter;
        }

        public override Encoding Encoding
        {
            get { return primary.Encoding; }
        }

        public override void Write(char value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void Write(string value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void WriteLine()
        {
            primary.WriteLine();
            secondary.WriteLine();
        }

        public override void WriteLine(string value)
        {
            primary.WriteLine(value);
            secondary.WriteLine(value);
        }

        public override void Flush()
        {
            primary.Flush();
            secondary.Flush();
        }
    }

    private sealed class ProgressDisplay : IDisposable
    {
        private readonly TextWriter outputWriter;
        private bool canRefresh;
        private bool initialized;
        private bool completed;
        private bool fallbackHeaderWritten;
        private int firstLineTop;

        public ProgressDisplay(TextWriter writer, bool refresh)
        {
            outputWriter = writer;
            canRefresh = refresh;
        }

        public void Update(string status, string detail)
        {
            if (completed)
            {
                return;
            }

            if (!canRefresh)
            {
                WriteFallbackHeader(status);
                return;
            }

            if (!EnsureInitialized())
            {
                WriteFallbackHeader(status);
                return;
            }

            Render(status, detail);
        }

        public void Complete(string status, string detail)
        {
            if (completed)
            {
                return;
            }

            completed = true;

            if (!canRefresh)
            {
                WriteFallbackHeader(status);
                if (!string.IsNullOrEmpty(detail))
                {
                    outputWriter.WriteLine("       " + detail);
                    outputWriter.Flush();
                }

                return;
            }

            if (!EnsureInitialized())
            {
                WriteFallbackHeader(status);
                if (!string.IsNullOrEmpty(detail))
                {
                    outputWriter.WriteLine("       " + detail);
                    outputWriter.Flush();
                }

                return;
            }

            Render(status, detail);
            MoveCursorBelowStatus();
        }

        public void Dispose()
        {
            if (!completed && initialized && canRefresh)
            {
                MoveCursorBelowStatus();
            }
        }

        private bool EnsureInitialized()
        {
            if (initialized)
            {
                return true;
            }

            try
            {
                outputWriter.WriteLine();
                outputWriter.WriteLine();
                outputWriter.Flush();
                firstLineTop = Math.Max(0, Console.CursorTop - 2);
                initialized = true;
                return true;
            }
            catch
            {
                canRefresh = false;
                return false;
            }
        }

        private void Render(string status, string detail)
        {
            try
            {
                WriteStatusLine(firstLineTop, status);
                WriteStatusLine(firstLineTop + 1, detail);
                Console.SetCursorPosition(0, firstLineTop + 1);
                outputWriter.Flush();
            }
            catch
            {
                canRefresh = false;
            }
        }

        private void WriteStatusLine(int top, string text)
        {
            Console.SetCursorPosition(0, top);
            outputWriter.Write(FitConsoleLine(text));
        }

        private string FitConsoleLine(string text)
        {
            string normalizedText = NormalizeStatusText(text);
            int width = GetConsoleLineWidth();
            if (normalizedText.Length > width)
            {
                if (width <= 3)
                {
                    normalizedText = normalizedText.Substring(0, width);
                }
                else
                {
                    normalizedText = normalizedText.Substring(0, width - 3) + "...";
                }
            }

            return normalizedText.PadRight(width);
        }

        private static string NormalizeStatusText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static int GetConsoleLineWidth()
        {
            try
            {
                return Math.Max(1, Console.BufferWidth - 1);
            }
            catch
            {
                return 79;
            }
        }

        private void MoveCursorBelowStatus()
        {
            try
            {
                Console.SetCursorPosition(0, firstLineTop + 2);
                outputWriter.Flush();
            }
            catch
            {
                canRefresh = false;
            }
        }

        private void WriteFallbackHeader(string status)
        {
            if (fallbackHeaderWritten || string.IsNullOrEmpty(status))
            {
                return;
            }

            outputWriter.WriteLine(status);
            outputWriter.Flush();
            fallbackHeaderWritten = true;
        }
    }

    private sealed class DailyLogFileWriter : TextWriter
    {
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private readonly string targetDir;
        private readonly string logDirectoryPath;
        private StreamWriter currentWriter;
        private DateTime currentDate = DateTime.MinValue;
        private string currentPath = string.Empty;

        public DailyLogFileWriter(string targetDirectory, string directoryPath)
        {
            targetDir = targetDirectory;
            logDirectoryPath = directoryPath;
        }

        public string CurrentPath
        {
            get
            {
                EnsureWriter();
                return currentPath;
            }
        }

        public override Encoding Encoding
        {
            get { return Utf8WithoutBom; }
        }

        public override void Write(char value)
        {
            EnsureWriter();
            currentWriter.Write(value);
        }

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            EnsureWriter();
            currentWriter.Write(value);
        }

        public override void WriteLine()
        {
            EnsureWriter();
            currentWriter.WriteLine();
        }

        public override void WriteLine(string value)
        {
            EnsureWriter();
            currentWriter.WriteLine(value);
        }

        public override void Flush()
        {
            if (currentWriter != null)
            {
                currentWriter.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && currentWriter != null)
            {
                currentWriter.Dispose();
                currentWriter = null;
            }

            base.Dispose(disposing);
        }

        private void EnsureWriter()
        {
            DateTime today = DateTime.Now.Date;
            if (currentWriter != null && today == currentDate)
            {
                return;
            }

            string nextPath = GetFullPath(Path.Combine(logDirectoryPath, LogFilePrefix + today.ToString(LogFileDateFormat) + LogFileExtension));
            AssertSafeManagedPath(targetDir, nextPath);
            Directory.CreateDirectory(logDirectoryPath);
            AssertSafeManagedPath(targetDir, nextPath);

            if (currentWriter != null)
            {
                currentWriter.Flush();
                currentWriter.Dispose();
            }

            currentDate = today;
            currentPath = nextPath;
            currentWriter = new StreamWriter(File.Open(currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Utf8WithoutBom);
            currentWriter.AutoFlush = true;
        }
    }

    private sealed class TimestampedFileWriter : TextWriter
    {
        private readonly TextWriter innerWriter;
        private bool isLineStart = true;

        public TimestampedFileWriter(TextWriter writer)
        {
            innerWriter = writer;
        }

        public override Encoding Encoding
        {
            get { return innerWriter.Encoding; }
        }

        public override void Write(char value)
        {
            if (isLineStart && value != '\r' && value != '\n')
            {
                innerWriter.Write("[");
                innerWriter.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                innerWriter.Write("] ");
                isLineStart = false;
            }

            innerWriter.Write(value);

            if (value == '\n')
            {
                isLineStart = true;
            }
            else if (value != '\r')
            {
                isLineStart = false;
            }
        }

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            foreach (char ch in value)
            {
                Write(ch);
            }
        }

        public override void WriteLine()
        {
            innerWriter.WriteLine();
            isLineStart = true;
        }

        public override void WriteLine(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                Write(value);
            }

            innerWriter.WriteLine();
            isLineStart = true;
        }

        public override void Flush()
        {
            innerWriter.Flush();
        }
    }
}
