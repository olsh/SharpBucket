﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using LibGit2Sharp;

namespace SharpBucketTests.GitHelpers
{
    /// <summary>
    /// Class that allow to build from scratch a test repository to perform unit tests against it.
    /// </summary>
    internal class TestRepositoryBuilder : IDisposable
    {
        private string WorkingDirectory { get; }

        private IGitCredentialsProvider GitCredentialsProvider { get; }

        public TestRepositoryBuilder(string repositoryUrl, IGitCredentialsProvider gitCredentialsProvider)
        {
            GitCredentialsProvider = gitCredentialsProvider ?? throw new ArgumentNullException(nameof(gitCredentialsProvider));

            // Create repo working dir
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "SharpBucketTestRepositories", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkingDirectory);

            // Clone the bitbucket repository
            try
            {
                var cloneOptions = new CloneOptions
                {
                    CredentialsProvider = gitCredentialsProvider.GetCredentials
                };
                Repository.Clone(repositoryUrl, WorkingDirectory, cloneOptions);
            }
            catch (Exception)
            {
                Directory.Delete(WorkingDirectory, true);
                throw;
            }
        }

        public TestRepositoryInfo FillRepository()
        {
            var info = new TestRepositoryInfo();

            using (var repository = new Repository(WorkingDirectory))
            {
                // create a fake tet signature that will be used to signed all commits
                var testSignature = new Signature("fakeTestUser", "fake@test.com", new DateTimeOffset(2017, 12, 31, 23, 59, 59, TimeSpan.Zero));

                // create first commit
                AddOrUpdateFile(repository, "readme.md", "This is a test repo generated by the SharpBucket unit tests");
                AddOrUpdateFile(repository, "src/fileToDelete.txt", "This is a file that will be delete in second commit to show some delete diff");
                AddOrUpdateFile(repository, "src/fileToChange.txt", "This is a file that will be changed in second commit to show some change diff\nCurrent state: unchanged");
                AddOrUpdateFile(repository, "src/fileToRename.txt", "This is a file that will be renamed in second commit to show some rename diff");
                AddOrUpdateFile(repository, "src/subDir/fileInADeepDirectory.txt", "This file is in a deep directory to perform advanced tests of the src resource.");
                var firstCommit = repository.Commit("Initial commit", testSignature, testSignature);
                info.FirstCommit = firstCommit.Sha;

                // now that first commit is done, push the head so it fully initiate the master branch
                // and Bitbucket will assume that it's our main branch since it's the first one that we push
                repository.Network.Push(repository.Head, new PushOptions { CredentialsProvider = GitCredentialsProvider.GetCredentials });
                var master = repository.Branches["master"];

                // do second commit
                Commands.Remove(repository, "src/fileToDelete.txt");
                AddOrUpdateFile(repository, "src/fileToChange.txt", "This is a file that will be changed in second commit to show some change diff\nCurrent state: changed");
                Commands.Move(repository, "src/fileToRename.txt", "fileWithNewName.txt");
                repository.Commit("Second commit which perform various type of operations", testSignature, testSignature);

                // create and fill branchToDecline
                CreateAndSwitchToNewBranch(repository, "branchToDecline");
                AddOrUpdateFile(repository, "badNewWork.txt", "a bad work that should become a pull request to decline");
                repository.Commit("bad work that will be declined", testSignature, testSignature);

                // create and fill branchToAccept
                Commands.Checkout(repository, master);
                CreateAndSwitchToNewBranch(repository, "branchToAccept");
                AddOrUpdateFile(repository, "src/goodNewWork.txt", "a good work that should become a pull request to accept");
                repository.Commit("first good work", testSignature, testSignature);
                AddOrUpdateFile(repository, "src/goodNewWork2.txt", "a second good work in the same pull request to have 2 commits in one pull request");
                repository.Commit("second good work", testSignature, testSignature);

                // Push All branches
                repository.Network.Push(repository.Branches, new PushOptions { CredentialsProvider = GitCredentialsProvider.GetCredentials });
            }

            return info;
        }

        private void AddOrUpdateFile(Repository repository, string fileName, string fileContent)
        {
            var dir = Path.GetDirectoryName(fileName);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory($"{WorkingDirectory}\\{dir}");
            }
            File.WriteAllText($"{WorkingDirectory}\\{fileName}", fileContent);
            Commands.Stage(repository, fileName);
        }

        private void CreateAndSwitchToNewBranch(Repository repository, string branchName)
        {
            var newBranch = repository.CreateBranch(branchName);
            repository.Branches.Update(newBranch, updater =>
            {
                updater.Remote = "origin";
                updater.UpstreamBranch = $"refs/heads/{branchName}";
            });
            Commands.Checkout(repository, newBranch);
        }

        public void Dispose()
        {
            Console.WriteLine($"Delete {WorkingDirectory}");
            DeleteGitDirectory(WorkingDirectory);
        }

        // From https://stackoverflow.com/questions/29098942/deletion-of-git-repository
        // source code here: https://github.com/libgit2/libgit2sharp/blob/vNext/LibGit2Sharp.Tests/TestHelpers/DirectoryHelper.cs#L46-L57
        private static void DeleteGitDirectory(string directoryPath)
        {
            // From http://stackoverflow.com/questions/329355/cannot-delete-directory-with-directory-deletepath-true/329502#329502

            if (!Directory.Exists(directoryPath))
            {
                return;
            }
            NormalizeAttributes(directoryPath);
            DeleteDirectory(directoryPath, maxAttempts: 5, initialTimeout: 16, timeoutFactor: 2);
        }

        private static void NormalizeAttributes(string directoryPath)
        {
            string[] filePaths = Directory.GetFiles(directoryPath);
            string[] subDirectoryPaths = Directory.GetDirectories(directoryPath);

            foreach (string filePath in filePaths)
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
            }
            foreach (string subDirectoryPath in subDirectoryPaths)
            {
                NormalizeAttributes(subDirectoryPath);
            }
            File.SetAttributes(directoryPath, FileAttributes.Normal);
        }

        private static readonly Type[] Whitelist = { typeof(IOException), typeof(UnauthorizedAccessException) };

        private static void DeleteDirectory(string directoryPath, int maxAttempts, int initialTimeout, int timeoutFactor)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(directoryPath, true);
                    return;
                }
                catch (Exception ex)
                {
                    var caughtExceptionType = ex.GetType();

                    if (!Whitelist.Any(knownExceptionType => knownExceptionType.IsAssignableFrom(caughtExceptionType)))
                    {
                        throw;
                    }

                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(initialTimeout * (int)Math.Pow(timeoutFactor, attempt - 1));
                        continue;
                    }

                    Console.WriteLine($@"
The directory '{Path.GetFullPath(directoryPath)}' could not be deleted ({maxAttempts} attempts were made)
This is due to a {caughtExceptionType}: {ex.Message}
Most of the time, this is due to an external process accessing the files in the temporary repositories created during the test runs, and keeping a handle on the directory, thus preventing the deletion of those files.
Known and common causes include:
- Windows Search Indexer (go to the Indexing Options, in the Windows Control Panel, and exclude the SharpBucketTestRepositories folder of your temp)
- Antivirus (exclude the SharpBucketTestRepositories folder of your temp from the paths scanned by your real-time antivirus)
- TortoiseGit (change the 'Icon Overlays' settings, e.g., adding the SharpBucketTestRepositories folder of your temp to 'Exclude paths').
  See documentation here: https://tortoisegit.org/docs/tortoisegit/tgit-dug-settings.html#tgit-dug-settings-overlay");
                }
            }
        }
    }
}
